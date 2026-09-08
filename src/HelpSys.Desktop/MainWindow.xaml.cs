using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using HelpSys.Models;
using HelpSys.Services;

namespace HelpSys;

public partial class MainWindow : Window
{
    private const double MinimumTargetConfidence = 0.72;
    private const double MinimumVisionConfidence = 0.84;
    private readonly UiAutomationScanner _scanner = new();
    private readonly ScreenCaptureService _screenCapture = new();
    private readonly GuidePlanner _fallbackPlanner = new();
    private readonly CloudGuideService _cloudGuide = new();
    private readonly GlobalHotKeyService _hotKey = new();
    private readonly UserActionObserver _actionObserver = new();
    private readonly SpeechInputService _speechInput = new();
    private readonly OverlayWindow _overlay = new();
    private readonly List<GuideHistoryItem> _history = [];

    private CancellationTokenSource? _sessionCts;
    private CancellationTokenSource? _voiceCts;
    private GuideDecision? _currentDecision;
    private UiElementCandidate? _currentTarget;
    private IReadOnlyList<UiElementCandidate> _stepBaseline = [];
    private Rect? _guidedBounds;
    private string? _activeRequest;
    private int _stepNumber;
    private int _doubleClickCount;
    private DateTime _lastGuidedClickUtc = DateTime.MinValue;
    private bool _planning;
    private bool _verifyingAction;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        SourceInitialized += OnSourceInitialized;
        Closing += OnClosing;
        _actionObserver.LeftClick += OnObservedLeftClick;
        _actionObserver.KeyReleased += OnObservedKeyReleased;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        PositionNearBottomRight();
        StateText.Text = "やりたいことを入力してください。Ctrl+Alt+Hで現在の画面へ呼び戻せます。";
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        _hotKey.Activated += (_, _) => ExpandAssistant();
        if (!_hotKey.Register(hwnd)) StateText.Text = "Ctrl+Alt+H は他のアプリが使用中です。";
    }

    private void PositionNearBottomRight() => MonitorPlacementService.MoveWindowToCursorMonitorBottomRight(this);

    private void ExpandAssistant()
    {
        Show();
        WindowState = WindowState.Normal;
        PositionNearBottomRight();
        Activate();
        RequestBox.Focus();
        RequestBox.CaretIndex = RequestBox.Text.Length;
    }

    private void DragHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        try { DragMove(); } catch (InvalidOperationException) { }
    }

    private async void GuideButton_Click(object sender, RoutedEventArgs e) => await StartNewSessionAsync();

    private async void RequestBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await StartNewSessionAsync();
    }

    private async void VoiceButton_Click(object sender, RoutedEventArgs e)
    {
        if (_voiceCts is not null)
        {
            _voiceCts.Cancel();
            return;
        }

        _voiceCts = new CancellationTokenSource();
        VoiceButton.Content = "停止";
        GuideButton.IsEnabled = false;
        StateText.Text = "音声を聞いています… 話し終わると入力欄に入ります。";

        try
        {
            var text = await _speechInput.RecognizeOnceAsync(_voiceCts.Token);
            if (!string.IsNullOrWhiteSpace(text))
            {
                RequestBox.Text = text;
                RequestBox.CaretIndex = RequestBox.Text.Length;
                StateText.Text = "音声を入力しました。必要なら修正して「案内」を押してください。";
            }
            else if (!_voiceCts.IsCancellationRequested)
            {
                StateText.Text = "音声を認識できませんでした。もう一度試すか、文字で入力してください。";
            }
        }
        catch (OperationCanceledException)
        {
            StateText.Text = "音声入力を停止しました。";
        }
        catch (Exception ex)
        {
            StateText.Text = $"音声入力を開始できません: {ex.Message}";
        }
        finally
        {
            _voiceCts?.Dispose();
            _voiceCts = null;
            VoiceButton.Content = "音声";
            GuideButton.IsEnabled = true;
            RequestBox.Focus();
        }
    }

    private async Task StartNewSessionAsync()
    {
        _voiceCts?.Cancel();
        var request = RequestBox.Text.Trim();
        if (request.Length == 0)
        {
            StateText.Text = "やりたいことを入力してください。";
            return;
        }

        EndSession();
        _activeRequest = request;
        _stepNumber = 0;
        _history.Clear();
        _sessionCts = new CancellationTokenSource();

        try { _actionObserver.Start(); }
        catch (Exception ex)
        {
            StateText.Text = $"操作の確認を開始できません: {ex.Message}";
            EndSession();
            return;
        }

        await AdvanceGuideAsync();
    }

    private async Task AdvanceGuideAsync()
    {
        if (_planning || _activeRequest is null || _sessionCts is null || _sessionCts.IsCancellationRequested) return;
        _planning = true;
        _currentDecision = null;
        _currentTarget = null;
        _stepBaseline = [];
        _guidedBounds = null;
        _doubleClickCount = 0;
        _overlay.Hide();
        GuideButton.IsEnabled = false;

        using var planningCts = CancellationTokenSource.CreateLinkedTokenSource(_sessionCts.Token);
        planningCts.CancelAfter(TimeSpan.FromSeconds(22));
        var cancellationToken = planningCts.Token;

        try
        {
            StateText.Text = "今の画面を確認しています…";
            var candidates = await _scanner.CaptureCandidatesAsync(420, cancellationToken);
            if (candidates.Count == 0)
            {
                if (!await TryVisionFallbackAsync(candidates, cancellationToken)) StopWithMessage("今の画面から、次に押す場所を確認できませんでした。");
                return;
            }

            StateText.Text = "次にすることを考えています…";
            GuideDecision decision;
            try
            {
                decision = await _cloudGuide.PlanAsync(_activeRequest, candidates, _history, cancellationToken);
            }
            catch (Exception cloudError) when (cloudError is HttpRequestException or TaskCanceledException or InvalidOperationException)
            {
                await RunConservativeFallbackAsync(_activeRequest, candidates, cancellationToken);
                return;
            }

            if (decision.Status.Equals("done", StringComparison.OrdinalIgnoreCase))
            {
                StopWithMessage(string.IsNullOrWhiteSpace(decision.Instruction) ? "目的の画面まで進めました。" : decision.Instruction);
                return;
            }

            if (decision.Status.Equals("clarify", StringComparison.OrdinalIgnoreCase))
            {
                StopWithMessage(decision.Question ?? "どれを使いたいか、もう少し教えてください。");
                return;
            }

            if (!decision.Status.Equals("target", StringComparison.OrdinalIgnoreCase) || decision.Confidence < MinimumTargetConfidence || string.IsNullOrWhiteSpace(decision.TargetId))
            {
                if (!await TryVisionFallbackAsync(candidates, cancellationToken)) StopWithMessage("今の画面では、次に押す場所をはっきり確認できませんでした。");
                return;
            }

            var target = candidates.FirstOrDefault(x => string.Equals(x.Id, decision.TargetId, StringComparison.Ordinal));
            if (target is null || !target.Interactable || target.Bounds.IsEmpty)
            {
                if (!await TryVisionFallbackAsync(candidates, cancellationToken)) StopWithMessage("案内する場所を今の画面で確認できませんでした。");
                return;
            }

            ShowStructuredTarget(decision, target, candidates);
        }
        catch (OperationCanceledException)
        {
            if (_sessionCts is { IsCancellationRequested: false }) StopWithMessage("画面の確認に時間がかかりすぎました。もう一度「案内」を押してください。");
        }
        catch (Exception ex)
        {
            StopWithMessage($"画面を確認できませんでした: {ex.Message}");
        }
        finally
        {
            _planning = false;
            GuideButton.IsEnabled = true;
        }
    }

    private void ShowStructuredTarget(GuideDecision decision, UiElementCandidate target, IReadOnlyList<UiElementCandidate> candidates)
    {
        _currentDecision = decision;
        _currentTarget = target;
        _stepBaseline = candidates;
        _guidedBounds = target.Bounds;
        var instruction = string.IsNullOrWhiteSpace(decision.Instruction) ? DefaultInstruction(decision.Action) : decision.Instruction;
        _overlay.ShowTarget(target.Bounds, instruction);
        StateText.Text = $"手順 {_stepNumber + 1}：{instruction}";
    }

    private async Task<bool> TryVisionFallbackAsync(IReadOnlyList<UiElementCandidate> candidates, CancellationToken cancellationToken)
    {
        if (_activeRequest is null) return false;
        StateText.Text = "画面の文字だけでは分からないため、見た目も確認しています…";
        var passwordBounds = candidates.Where(x => x.Password).Select(x => x.Bounds).ToArray();

        var previousOpacity = Opacity;
        ScreenCaptureFrame frame;
        try
        {
            Opacity = 0;
            await Task.Delay(90, cancellationToken);
            frame = _screenCapture.Capture(passwordBounds);
        }
        finally
        {
            Opacity = previousOpacity;
        }

        VisionGuideDecision decision;
        try
        {
            decision = await _cloudGuide.PlanVisionAsync(_activeRequest, frame, _history, cancellationToken);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return false;
        }

        if (decision.Status.Equals("done", StringComparison.OrdinalIgnoreCase))
        {
            StopWithMessage(string.IsNullOrWhiteSpace(decision.Instruction) ? "目的の画面まで進めました。" : decision.Instruction);
            return true;
        }

        if (decision.Status.Equals("clarify", StringComparison.OrdinalIgnoreCase))
        {
            StopWithMessage(decision.Question ?? "どれを使いたいか教えてください。");
            return true;
        }

        if (!decision.Status.Equals("target", StringComparison.OrdinalIgnoreCase) || decision.Confidence < MinimumVisionConfidence) return false;

        var bounds = frame.MapNormalizedBounds(decision.X, decision.Y, decision.Width, decision.Height);
        if (bounds.IsEmpty || bounds.Width < 8 || bounds.Height < 8) return false;

        var instruction = string.IsNullOrWhiteSpace(decision.Instruction)
            ? "青い枠で囲まれた場所を、マウスの左ボタンで1回クリックしてください。"
            : decision.Instruction;
        _currentDecision = new GuideDecision("target", "vision-target", "left_click", instruction, null, null, decision.Confidence);
        _currentTarget = null;
        _stepBaseline = candidates;
        _guidedBounds = bounds;
        _overlay.ShowTarget(bounds, instruction);
        StateText.Text = $"手順 {_stepNumber + 1}：{instruction}";
        return true;
    }

    private async Task RunConservativeFallbackAsync(string request, IReadOnlyList<UiElementCandidate> baseline, CancellationToken cancellationToken)
    {
        var plan = _fallbackPlanner.CreateFirstStep(request);
        if (plan.TargetHints.Count == 0)
        {
            StopWithMessage(plan.Instruction);
            return;
        }

        var target = await _scanner.FindBestTargetAsync(plan.TargetHints, cancellationToken);
        if (target is null || target.Score < 30)
        {
            StopWithMessage("通信できず、押す場所も安全に確認できませんでした。");
            return;
        }

        var instruction = "青い枠で囲まれた場所を、マウスの左ボタンで1回クリックしてください。";
        _currentDecision = new GuideDecision("target", "local-fallback", "left_click", instruction, null, null, 0.70);
        _currentTarget = null;
        _stepBaseline = baseline;
        _guidedBounds = target.Bounds;
        _overlay.ShowTarget(target.Bounds, instruction);
        StateText.Text = $"手順 {_stepNumber + 1}：{instruction}";
    }

    private async void OnObservedLeftClick(Point point)
    {
        if (_planning || _verifyingAction || _currentDecision is null || _guidedBounds is null) return;
        var action = _currentDecision.Action;
        if (!action.Equals("left_click", StringComparison.OrdinalIgnoreCase) && !action.Equals("double_click", StringComparison.OrdinalIgnoreCase)) return;

        var bounds = _guidedBounds.Value;
        bounds.Inflate(7, 7);
        if (!bounds.Contains(point)) return;

        if (action.Equals("double_click", StringComparison.OrdinalIgnoreCase))
        {
            var now = DateTime.UtcNow;
            if ((now - _lastGuidedClickUtc).TotalMilliseconds > 1100) _doubleClickCount = 0;
            _lastGuidedClickUtc = now;
            _doubleClickCount++;
            if (_doubleClickCount < 2)
            {
                StateText.Text = "同じ青い枠の場所を、もう1回すばやく左クリックしてください。";
                return;
            }
        }

        await CompleteCurrentStepAsync();
    }

    private async void OnObservedKeyReleased(int virtualKey)
    {
        if (_planning || _verifyingAction || _currentDecision is null) return;
        if (_currentDecision.Action.Equals("type_text", StringComparison.OrdinalIgnoreCase))
        {
            var expected = string.IsNullOrWhiteSpace(_currentDecision.Key) ? "Enter" : _currentDecision.Key;
            if (MatchesVirtualKey(expected, virtualKey)) await CompleteCurrentStepAsync();
        }
        else if (_currentDecision.Action.Equals("press_key", StringComparison.OrdinalIgnoreCase) && MatchesVirtualKey(_currentDecision.Key, virtualKey))
        {
            await CompleteCurrentStepAsync();
        }
    }

    private async Task CompleteCurrentStepAsync()
    {
        if (_verifyingAction || _currentDecision is null || _activeRequest is null || _sessionCts is null) return;
        _verifyingAction = true;
        var decision = _currentDecision;
        var targetName = _currentTarget is null ? "画面上の案内場所" : DisplayName(_currentTarget.Name, _currentTarget.ControlType);

        try
        {
            StateText.Text = "操作の結果で画面が変わったか確認しています…";
            var changed = await WaitForStateTransitionAsync(decision.Action, _stepBaseline, _sessionCts.Token);
            if (!changed)
            {
                _doubleClickCount = 0;
                StateText.Text = decision.Action.Equals("double_click", StringComparison.OrdinalIgnoreCase)
                    ? "まだ画面が変わっていません。青い枠の場所を、左ボタンですばやく2回クリックしてください。"
                    : "まだ画面が変わっていません。青い枠の場所を、もう一度ゆっくり操作してください。";
                return;
            }

            _history.Add(new GuideHistoryItem(++_stepNumber, decision.Action, targetName, decision.Instruction));
            if (_history.Count > 12) _history.RemoveAt(0);
            _currentDecision = null;
            _currentTarget = null;
            _stepBaseline = [];
            _guidedBounds = null;
            _doubleClickCount = 0;
            _overlay.Hide();
            StateText.Text = "画面が変わったことを確認しました。次を確認しています…";
            await Task.Delay(250, _sessionCts.Token);
            await AdvanceGuideAsync();
        }
        catch (OperationCanceledException) { }
        finally
        {
            _verifyingAction = false;
        }
    }

    private async Task<bool> WaitForStateTransitionAsync(string action, IReadOnlyList<UiElementCandidate> before, CancellationToken cancellationToken)
    {
        var needsStrongChange = action.Equals("double_click", StringComparison.OrdinalIgnoreCase);
        await Task.Delay(needsStrongChange ? 1400 : 450, cancellationToken);

        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(6.5))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var after = await _scanner.CaptureCandidatesAsync(420, cancellationToken);
            var changed = needsStrongChange ? HasStrongContentChange(before, after) : HasMeaningfulChange(before, after);
            if (changed)
            {
                await Task.Delay(needsStrongChange ? 750 : 450, cancellationToken);
                return true;
            }
            await Task.Delay(350, cancellationToken);
        }
        return false;
    }

    private static bool HasMeaningfulChange(IReadOnlyList<UiElementCandidate> before, IReadOnlyList<UiElementCandidate> after)
    {
        if (before.Count == 0) return after.Count > 0;
        if (HasWindowSetChange(before, after)) return true;

        var beforeFocus = before.FirstOrDefault(x => x.Focused);
        var afterFocus = after.FirstOrDefault(x => x.Focused);
        if (beforeFocus is not null && afterFocus is not null && !StableKey(beforeFocus).Equals(StableKey(afterFocus), StringComparison.Ordinal)) return true;

        return HasLargeContentChange(before, after);
    }

    private static bool HasStrongContentChange(IReadOnlyList<UiElementCandidate> before, IReadOnlyList<UiElementCandidate> after)
    {
        if (before.Count == 0) return after.Count > 0;
        if (HasWindowSetChange(before, after)) return true;

        var beforeFocusedProcess = before.FirstOrDefault(x => x.Focused)?.ProcessName ?? string.Empty;
        var afterFocusedProcess = after.FirstOrDefault(x => x.Focused)?.ProcessName ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(afterFocusedProcess) && !beforeFocusedProcess.Equals(afterFocusedProcess, StringComparison.OrdinalIgnoreCase)) return true;

        return HasLargeContentChange(before, after);
    }

    private static bool HasWindowSetChange(IReadOnlyList<UiElementCandidate> before, IReadOnlyList<UiElementCandidate> after)
    {
        var beforeWindows = before.Where(x => x.ControlType.Equals("Window", StringComparison.OrdinalIgnoreCase)).Select(StableKey).ToHashSet(StringComparer.Ordinal);
        var afterWindows = after.Where(x => x.ControlType.Equals("Window", StringComparison.OrdinalIgnoreCase)).Select(StableKey).ToHashSet(StringComparer.Ordinal);
        return !beforeWindows.SetEquals(afterWindows);
    }

    private static bool HasLargeContentChange(IReadOnlyList<UiElementCandidate> before, IReadOnlyList<UiElementCandidate> after)
    {
        var beforeKeys = before.Select(StateKey).ToHashSet(StringComparer.Ordinal);
        var afterKeys = after.Select(StateKey).ToHashSet(StringComparer.Ordinal);
        if (Math.Abs(beforeKeys.Count - afterKeys.Count) >= 10) return true;
        if (beforeKeys.Count == 0 || afterKeys.Count == 0) return beforeKeys.Count != afterKeys.Count;

        var overlap = beforeKeys.Count(x => afterKeys.Contains(x));
        var similarity = overlap / (double)Math.Max(beforeKeys.Count, afterKeys.Count);
        return similarity < 0.82;
    }

    private static string StableKey(UiElementCandidate x) => $"{x.ProcessName}|{x.ControlType}|{x.ClassName}|{x.Name}";
    private static string StateKey(UiElementCandidate x) => $"{x.ProcessName}|{x.ControlType}|{x.Name}|{x.AutomationId}|{x.ClassName}";

    private static bool MatchesVirtualKey(string? key, int virtualKey)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;
        return key.Trim().ToLowerInvariant() switch
        {
            "enter" or "return" => virtualKey == 0x0D,
            "tab" => virtualKey == 0x09,
            "escape" or "esc" => virtualKey == 0x1B,
            "space" => virtualKey == 0x20,
            "delete" or "del" => virtualKey == 0x2E,
            "backspace" => virtualKey == 0x08,
            _ => false
        };
    }

    private static string DefaultInstruction(string action) => action.ToLowerInvariant() switch
    {
        "double_click" => "青い枠で囲まれた場所を、マウスの左ボタンですばやく2回クリックしてください。",
        "type_text" => "青い枠の入力欄に文字を入力し、キーボードの Enter（エンター）キーを1回押してください。",
        "press_key" => "案内されたキーボードのキーを1回押してください。",
        _ => "青い枠で囲まれた場所を、マウスの左ボタンで1回クリックしてください。"
    };

    private static string DisplayName(string name, string controlType) => string.IsNullOrWhiteSpace(name) ? controlType.Replace("ControlType.", string.Empty) : name;

    private void StopWithMessage(string message)
    {
        _overlay.Hide();
        _currentDecision = null;
        _currentTarget = null;
        _stepBaseline = [];
        _guidedBounds = null;
        _doubleClickCount = 0;
        _verifyingAction = false;
        _actionObserver.Stop();
        _sessionCts?.Cancel();
        _activeRequest = null;
        StateText.Text = message;
    }

    private void EndSession()
    {
        _overlay.Hide();
        _currentDecision = null;
        _currentTarget = null;
        _stepBaseline = [];
        _guidedBounds = null;
        _doubleClickCount = 0;
        _verifyingAction = false;
        _actionObserver.Stop();
        _sessionCts?.Cancel();
        _sessionCts?.Dispose();
        _sessionCts = null;
        _activeRequest = null;
        _history.Clear();
        _planning = false;
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        EndSession();
        StateText.Text = "案内を消しました。次にやりたいことを入力してください。";
    }

    private void ExitButton_Click(object sender, RoutedEventArgs e) => Close();

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _voiceCts?.Cancel();
        _voiceCts?.Dispose();
        _voiceCts = null;
        EndSession();
        _speechInput.Dispose();
        _actionObserver.Dispose();
        _cloudGuide.Dispose();
        _hotKey.Dispose();
        _overlay.Close();
    }
}

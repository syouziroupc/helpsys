using System.ComponentModel;
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
    private readonly OverlayWindow _overlay = new();
    private readonly List<GuideHistoryItem> _history = [];

    private CancellationTokenSource? _sessionCts;
    private GuideDecision? _currentDecision;
    private UiElementCandidate? _currentTarget;
    private Rect? _guidedBounds;
    private string? _activeRequest;
    private int _stepNumber;
    private bool _planning;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        SourceInitialized += OnSourceInitialized;
        Closing += OnClosing;
        _actionObserver.LeftClick += OnObservedLeftClick;
        _actionObserver.KeyReleased += OnObservedKeyReleased;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => CollapseToLauncher();

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        _hotKey.Activated += (_, _) => ExpandAssistant();
        if (!_hotKey.Register(hwnd)) StateText.Text = "Ctrl+Alt+H は他のアプリが使用中です。";
    }

    private void PositionNearBottomRight()
    {
        Left = SystemParameters.WorkArea.Right - Width - 18;
        Top = SystemParameters.WorkArea.Bottom - Height - 18;
    }

    private void ExpandAssistant()
    {
        Width = 390;
        Height = 214;
        LauncherButton.Visibility = Visibility.Collapsed;
        AssistantPanel.Visibility = Visibility.Visible;
        PositionNearBottomRight();
        Show();
        WindowState = WindowState.Normal;
        Activate();
        RequestBox.Focus();
        StateText.Text = "何をしたいですか？";
    }

    private void CollapseToLauncher()
    {
        EndSession();
        AssistantPanel.Visibility = Visibility.Collapsed;
        LauncherButton.Visibility = Visibility.Visible;
        Width = 94;
        Height = 58;
        PositionNearBottomRight();
    }

    private void LauncherButton_Click(object sender, RoutedEventArgs e) => ExpandAssistant();
    private async void GuideButton_Click(object sender, RoutedEventArgs e) => await StartNewSessionAsync();

    private async void RequestBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await StartNewSessionAsync();
    }

    private async Task StartNewSessionAsync()
    {
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
            StateText.Text = $"操作観測を開始できません: {ex.Message}";
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
        _guidedBounds = null;
        _overlay.Hide();
        GuideButton.IsEnabled = false;

        using var planningCts = CancellationTokenSource.CreateLinkedTokenSource(_sessionCts.Token);
        planningCts.CancelAfter(TimeSpan.FromSeconds(22));
        var cancellationToken = planningCts.Token;

        try
        {
            StateText.Text = "現在の画面を確認しています…";
            var candidates = await _scanner.CaptureCandidatesAsync(360, cancellationToken);
            if (candidates.Count == 0)
            {
                if (!await TryVisionFallbackAsync(candidates, cancellationToken)) StopWithMessage("操作対象を特定できませんでした。");
                return;
            }

            StateText.Text = $"{candidates.Count}個の画面要素から次の操作を判断しています…";
            GuideDecision decision;
            try
            {
                decision = await _cloudGuide.PlanAsync(_activeRequest, candidates, _history, cancellationToken);
            }
            catch (Exception cloudError) when (cloudError is HttpRequestException or TaskCanceledException or InvalidOperationException)
            {
                await RunConservativeFallbackAsync(_activeRequest, cancellationToken);
                return;
            }

            if (decision.Status.Equals("done", StringComparison.OrdinalIgnoreCase))
            {
                StopWithMessage(string.IsNullOrWhiteSpace(decision.Instruction) ? "この作業は完了しています。" : decision.Instruction);
                return;
            }

            if (decision.Status.Equals("clarify", StringComparison.OrdinalIgnoreCase))
            {
                StopWithMessage(decision.Question ?? "やりたい操作をもう少し具体的に教えてください。");
                return;
            }

            if (!decision.Status.Equals("target", StringComparison.OrdinalIgnoreCase) || decision.Confidence < MinimumTargetConfidence || string.IsNullOrWhiteSpace(decision.TargetId))
            {
                if (!await TryVisionFallbackAsync(candidates, cancellationToken)) StopWithMessage("今の画面では、次の操作を十分な確度で特定できませんでした。");
                return;
            }

            var target = candidates.FirstOrDefault(x => string.Equals(x.Id, decision.TargetId, StringComparison.Ordinal));
            if (target is null || target.Bounds.IsEmpty)
            {
                if (!await TryVisionFallbackAsync(candidates, cancellationToken)) StopWithMessage("AIが選んだ対象を現在画面で再確認できませんでした。");
                return;
            }

            ShowStructuredTarget(decision, target);
        }
        catch (OperationCanceledException)
        {
            if (_sessionCts is { IsCancellationRequested: false }) StopWithMessage("画面確認がタイムアウトしました。");
        }
        catch (Exception ex)
        {
            StopWithMessage($"画面確認エラー: {ex.Message}");
        }
        finally
        {
            _planning = false;
            GuideButton.IsEnabled = true;
        }
    }

    private void ShowStructuredTarget(GuideDecision decision, UiElementCandidate target)
    {
        _currentDecision = decision;
        _currentTarget = target;
        _guidedBounds = target.Bounds;
        var instruction = string.IsNullOrWhiteSpace(decision.Instruction) ? DefaultInstruction(decision.Action) : decision.Instruction;
        _overlay.ShowTarget(target.Bounds, instruction);
        StateText.Text = $"手順 {_stepNumber + 1}: {DisplayName(target.Name, target.ControlType)}　確度 {decision.Confidence:P0}";
    }

    private async Task<bool> TryVisionFallbackAsync(IReadOnlyList<UiElementCandidate> candidates, CancellationToken cancellationToken)
    {
        if (_activeRequest is null) return false;
        StateText.Text = "構造情報だけでは特定できないため、画面画像を確認しています…";
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
            StopWithMessage(string.IsNullOrWhiteSpace(decision.Instruction) ? "この作業は完了しています。" : decision.Instruction);
            return true;
        }

        if (decision.Status.Equals("clarify", StringComparison.OrdinalIgnoreCase))
        {
            StopWithMessage(decision.Question ?? "どの操作をしたいか、もう少し具体的に教えてください。");
            return true;
        }

        if (!decision.Status.Equals("target", StringComparison.OrdinalIgnoreCase) || decision.Confidence < MinimumVisionConfidence) return false;

        var bounds = frame.MapNormalizedBounds(decision.X, decision.Y, decision.Width, decision.Height);
        if (bounds.IsEmpty || bounds.Width < 8 || bounds.Height < 8) return false;

        var instruction = string.IsNullOrWhiteSpace(decision.Instruction) ? "ここを左クリックしてください。" : decision.Instruction;
        _currentDecision = new GuideDecision("target", "vision-target", "left_click", instruction, null, null, decision.Confidence);
        _currentTarget = null;
        _guidedBounds = bounds;
        _overlay.ShowTarget(bounds, instruction);
        StateText.Text = $"手順 {_stepNumber + 1}: {decision.Label ?? "画像上の対象"}　画像確度 {decision.Confidence:P0}";
        return true;
    }

    private async Task RunConservativeFallbackAsync(string request, CancellationToken cancellationToken)
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
            StopWithMessage("判断APIに接続できず、安全に案内できる対象も特定できませんでした。");
            return;
        }

        _currentDecision = new GuideDecision("target", "local-fallback", "left_click", plan.Instruction, null, null, 0.70);
        _currentTarget = null;
        _guidedBounds = target.Bounds;
        _overlay.ShowTarget(target.Bounds, plan.Instruction);
        StateText.Text = $"ローカル案内: {DisplayName(target.Name, target.ControlType)}";
    }

    private async void OnObservedLeftClick(Point point)
    {
        if (_planning || _currentDecision is null || _guidedBounds is null) return;
        if (!_currentDecision.Action.Equals("left_click", StringComparison.OrdinalIgnoreCase)) return;
        var bounds = _guidedBounds.Value;
        bounds.Inflate(5, 5);
        if (!bounds.Contains(point)) return;
        await CompleteCurrentStepAsync();
    }

    private async void OnObservedKeyReleased(int virtualKey)
    {
        if (_planning || _currentDecision is null) return;
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
        if (_currentDecision is null || _activeRequest is null) return;
        var decision = _currentDecision;
        var targetName = _currentTarget is null ? "visual/local target" : DisplayName(_currentTarget.Name, _currentTarget.ControlType);
        _history.Add(new GuideHistoryItem(++_stepNumber, decision.Action, targetName, decision.Instruction));
        if (_history.Count > 12) _history.RemoveAt(0);
        _currentDecision = null;
        _currentTarget = null;
        _guidedBounds = null;
        _overlay.Hide();
        StateText.Text = "操作を確認しました。次の手順を確認しています…";
        await Task.Delay(550);
        await AdvanceGuideAsync();
    }

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
        "type_text" => "この欄に入力して、Enterキーを押してください。",
        "press_key" => "指定されたキーを押してください。",
        _ => "ここを左クリックしてください。"
    };

    private static string DisplayName(string name, string controlType) => string.IsNullOrWhiteSpace(name) ? controlType.Replace("ControlType.", string.Empty) : name;

    private void StopWithMessage(string message)
    {
        _overlay.Hide();
        _currentDecision = null;
        _currentTarget = null;
        _guidedBounds = null;
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
        _guidedBounds = null;
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
        StateText.Text = "何をしたいですか？";
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => CollapseToLauncher();

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        EndSession();
        _actionObserver.Dispose();
        _cloudGuide.Dispose();
        _hotKey.Dispose();
        _overlay.Close();
    }
}

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
    private readonly CloudGuideService _cloudGuide = new();
    private readonly SystemContextService _systemContext = new();
    private readonly GlobalHotKeyService _hotKey = new();
    private readonly UserActionObserver _actionObserver = new();
    private readonly SpeechInputService _speechInput = new();
    private readonly SpeechOutputService _speechOutput = new();
    private readonly OverlayWindow _overlay = new();
    private readonly KeyHintWindow _keyHint = new();
    private readonly List<GuideHistoryItem> _history = [];

    private CancellationTokenSource? _sessionCts;
    private CancellationTokenSource? _voiceCts;
    private GuideDecision? _currentDecision;
    private UiElementCandidate? _currentTarget;
    private IReadOnlyList<UiElementCandidate> _stepBaseline = [];
    private SystemContextSnapshot? _stepSystemBaseline;
    private Rect? _guidedBounds;
    private string? _activeRequest;
    private string? _originalRequest;
    private string? _clarificationQuestion;
    private string? _lastInstruction;
    private int _stepNumber;
    private int _doubleClickCount;
    private int _consecutiveFailures;
    private DateTime _lastGuidedClickUtc = DateTime.MinValue;
    private bool _planning;
    private bool _verifyingAction;
    private bool _awaitingClarification;
    private bool _forceVisionNext;

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
        SetState("やりたいことを入力してください。Ctrl+Alt+Hで現在の画面へ呼び戻せます。", speak: false);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        _hotKey.Activated += (_, _) => ExpandAssistant();
        if (!_hotKey.Register(hwnd)) SetState("Ctrl+Alt+H は他のアプリが使用中です。", speak: false);
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

    private async void GuideButton_Click(object sender, RoutedEventArgs e) => await StartOrContinueSessionAsync();

    private async void RequestBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await StartOrContinueSessionAsync();
    }

    private void SpeakButton_Click(object sender, RoutedEventArgs e)
    {
        _speechOutput.Speak(_lastInstruction ?? StateText.Text);
    }

    private async void VoiceButton_Click(object sender, RoutedEventArgs e)
    {
        if (_planning || _verifyingAction) return;

        if (_voiceCts is not null)
        {
            _voiceCts.Cancel();
            return;
        }

        _voiceCts = new CancellationTokenSource();
        VoiceButton.Content = "停止";
        GuideButton.IsEnabled = false;
        SetState("音声を聞いています。ゆっくり話してください。", speak: false);

        try
        {
            var text = await _speechInput.RecognizeOnceAsync(_voiceCts.Token);
            if (!string.IsNullOrWhiteSpace(text))
            {
                RequestBox.Text = text;
                RequestBox.CaretIndex = RequestBox.Text.Length;
                SetState(_awaitingClarification ? "回答を聞き取りました。「答える」を押してください。" : "音声を入力しました。必要なら直してから「案内」を押してください。", speak: false);
            }
            else if (!_voiceCts.IsCancellationRequested)
            {
                SetState("音声を聞き取れませんでした。もう一度試すか、文字で入力してください。", speak: true);
            }
        }
        catch (OperationCanceledException)
        {
            SetState("音声入力を停止しました。", speak: false);
        }
        catch (Exception ex)
        {
            SetState($"音声入力を開始できません: {ex.Message}", speak: true);
        }
        finally
        {
            _voiceCts?.Dispose();
            _voiceCts = null;
            VoiceButton.Content = "音声";
            GuideButton.IsEnabled = !_planning;
            RequestBox.Focus();
        }
    }

    private async Task StartOrContinueSessionAsync()
    {
        // Do not let Enter or another UI path start a second session while the previous async
        // planner is still unwinding. This prevents an old cancellation from killing a new goal.
        if (_planning || _verifyingAction) return;

        _voiceCts?.Cancel();
        var text = RequestBox.Text.Trim();
        if (text.Length == 0)
        {
            SetState(_awaitingClarification ? (_clarificationQuestion ?? "質問への答えを入力してください。") : "やりたいことを入力してください。", speak: true);
            return;
        }

        if (_awaitingClarification && _activeRequest is not null && _sessionCts is not null)
        {
            _history.Add(new GuideHistoryItem(_stepNumber, "clarification_answer", text, _clarificationQuestion ?? "確認質問"));
            _activeRequest += $"\n利用者からの追加回答: {text}";
            _awaitingClarification = false;
            _clarificationQuestion = null;
            GuideButton.Content = "案内";
            RequestBox.Text = _originalRequest ?? _activeRequest;
            RequestBox.CaretIndex = RequestBox.Text.Length;
            try { _actionObserver.Start(); } catch { }
            await AdvanceGuideAsync();
            return;
        }

        EndSession();
        _originalRequest = text;
        _activeRequest = text;
        _stepNumber = 0;
        _history.Clear();
        _sessionCts = new CancellationTokenSource();

        try { _actionObserver.Start(); }
        catch (Exception ex)
        {
            SetState($"操作の確認を開始できません: {ex.Message}", speak: true);
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
        _stepSystemBaseline = null;
        _guidedBounds = null;
        _doubleClickCount = 0;
        _overlay.Hide();
        _keyHint.Hide();
        GuideButton.IsEnabled = false;

        using var planningCts = CancellationTokenSource.CreateLinkedTokenSource(_sessionCts.Token);
        // A structured inference can legitimately be followed by a vision fallback. The former
        // 24-second end-to-end budget could expire during that second phase even on a healthy
        // connection. Keep a finite bound, but leave enough room for both phases.
        planningCts.CancelAfter(TimeSpan.FromSeconds(40));
        var cancellationToken = planningCts.Token;

        try
        {
            SetState("今の画面と、開いているアプリを確認しています…", speak: false);
            var candidates = await _scanner.CaptureCandidatesAsync(420, cancellationToken);
            var systemContext = _systemContext.Capture();

            if (_forceVisionNext)
            {
                _forceVisionNext = false;
                if (!await TryVisionFallbackAsync(candidates, systemContext, cancellationToken))
                    WaitForClarification("操作のあとで画面を確認できませんでした。今、画面に何が表示されているか短く教えてください。");
                return;
            }

            if (candidates.Count == 0)
            {
                if (!await TryVisionFallbackAsync(candidates, systemContext, cancellationToken))
                    StopWithMessage("今の画面から次の操作を安全に決められませんでした。");
                return;
            }

            SetState("次にすることを考えています…", speak: false);
            GuideDecision decision;
            try
            {
                decision = await _cloudGuide.PlanAsync(_activeRequest, candidates, _history, systemContext, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (HttpRequestException)
            {
                StopWithMessage("案内サーバーとの通信に失敗しました。間違った場所を案内しないため、ここで止めます。");
                return;
            }
            catch (InvalidOperationException)
            {
                StopWithMessage("案内サービスから正常な応答を受け取れませんでした。間違った場所を案内しないため、ここで止めます。");
                return;
            }

            if (decision.Status.Equals("done", StringComparison.OrdinalIgnoreCase))
            {
                StopWithMessage(string.IsNullOrWhiteSpace(decision.Instruction) ? "目的の画面まで進めました。" : decision.Instruction);
                return;
            }

            if (decision.Status.Equals("clarify", StringComparison.OrdinalIgnoreCase))
            {
                WaitForClarification(decision.Question ?? "どれを使いたいか教えてください。");
                return;
            }

            if (!decision.Status.Equals("target", StringComparison.OrdinalIgnoreCase) || decision.Confidence < MinimumTargetConfidence)
            {
                if (!await TryVisionFallbackAsync(candidates, systemContext, cancellationToken))
                    StopWithMessage("今の画面では、次にすることを安全に決められませんでした。");
                return;
            }

            if (decision.Action.Equals("press_key", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(decision.TargetId))
            {
                ShowKeyboardGuide(decision, candidates, systemContext);
                return;
            }

            if (string.IsNullOrWhiteSpace(decision.TargetId))
            {
                if (!await TryVisionFallbackAsync(candidates, systemContext, cancellationToken))
                    StopWithMessage("案内する場所を確認できませんでした。");
                return;
            }

            var target = candidates.FirstOrDefault(x => string.Equals(x.Id, decision.TargetId, StringComparison.Ordinal));
            if (target is null || !target.Interactable || target.Bounds.IsEmpty)
            {
                if (!await TryVisionFallbackAsync(candidates, systemContext, cancellationToken))
                    StopWithMessage("案内する場所を今の画面で確認できませんでした。");
                return;
            }

            var freshTarget = await _scanner.RevalidateCandidateAsync(target, cancellationToken);
            if (freshTarget is null)
            {
                if (!await TryVisionFallbackAsync(candidates, systemContext, cancellationToken))
                    StopWithMessage("画面が動いたため、押す場所をもう一度確認できませんでした。");
                return;
            }

            ShowStructuredTarget(decision, freshTarget, candidates, systemContext);
        }
        catch (OperationCanceledException)
        {
            if (_sessionCts is { IsCancellationRequested: false })
                StopWithMessage("画面の確認に時間がかかりすぎました。もう一度「案内」を押してください。");
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

    private void ShowStructuredTarget(GuideDecision decision, UiElementCandidate target, IReadOnlyList<UiElementCandidate> candidates, SystemContextSnapshot systemContext)
    {
        _currentDecision = decision;
        _currentTarget = target;
        _stepBaseline = candidates;
        _stepSystemBaseline = systemContext;
        _guidedBounds = target.Bounds;
        var instruction = string.IsNullOrWhiteSpace(decision.Instruction) ? DefaultInstruction(decision.Action) : decision.Instruction;
        _overlay.ShowTarget(target.Bounds, instruction);
        ShowInstruction(instruction);
    }

    private void ShowKeyboardGuide(GuideDecision decision, IReadOnlyList<UiElementCandidate> candidates, SystemContextSnapshot systemContext)
    {
        _currentDecision = decision;
        _currentTarget = null;
        _stepBaseline = candidates;
        _stepSystemBaseline = systemContext;
        _guidedBounds = null;
        _overlay.Hide();
        var instruction = string.IsNullOrWhiteSpace(decision.Instruction) ? DefaultInstruction("press_key") : decision.Instruction;
        _keyHint.ShowKeys(decision.Key, instruction);
        ShowInstruction(instruction);
    }

    private async Task<bool> TryVisionFallbackAsync(IReadOnlyList<UiElementCandidate> candidates, SystemContextSnapshot systemContext, CancellationToken cancellationToken)
    {
        if (_activeRequest is null) return false;
        SetState("画面の文字だけでは分からないため、見た目も確認しています…", speak: false);
        var passwordBounds = candidates.Where(x => x.Password).Select(x => x.Bounds).ToArray();

        _overlay.Hide();
        _keyHint.Hide();
        var previousOpacity = Opacity;
        ScreenCaptureFrame frame;
        try
        {
            Opacity = 0;
            await Task.Delay(100, cancellationToken);
            frame = await _screenCapture.CaptureAsync(passwordBounds, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidOperationException)
        {
            // Screenshot privacy validation failed. Do not send a partially protected image.
            return false;
        }
        finally
        {
            Opacity = previousOpacity;
        }

        VisionGuideDecision decision;
        try
        {
            decision = await _cloudGuide.PlanVisionAsync(_activeRequest, frame, _history, systemContext, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (error is HttpRequestException or InvalidOperationException)
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
            WaitForClarification(decision.Question ?? "どれを使いたいか教えてください。");
            return true;
        }

        if (!decision.Status.Equals("target", StringComparison.OrdinalIgnoreCase) || decision.Confidence < MinimumVisionConfidence) return false;

        var bounds = frame.MapNormalizedBounds(decision.X, decision.Y, decision.Width, decision.Height);
        if (bounds.IsEmpty || bounds.Width < 8 || bounds.Height < 8) return false;
        var snapped = await _scanner.SnapToAccessibleBoundsAsync(bounds, cancellationToken);

        // Never speak or draw a raw vision coordinate. It must reconcile with a current,
        // actually interactable Windows accessibility element before it can become guidance.
        if (snapped is not { } accessible || accessible.IsEmpty)
        {
            _history.Add(new GuideHistoryItem(_stepNumber, "vision_target_rejected", "画像候補", "画像AIの座標に現在押せるWindows要素が無いため、発話前に破棄した。"));
            if (_history.Count > 12) _history.RemoveAt(0);
            return false;
        }
        bounds = accessible;

        var instruction = string.IsNullOrWhiteSpace(decision.Instruction)
            ? "青い枠で囲まれた場所を、マウスの左ボタンで1回押してください。"
            : decision.Instruction;
        _currentDecision = new GuideDecision("target", "vision-target", "left_click", instruction, null, null, decision.Confidence);
        _currentTarget = null;
        _stepBaseline = candidates;
        _stepSystemBaseline = systemContext;
        _guidedBounds = bounds;
        _validatedVisionInstruction = null;
        _overlay.ShowTarget(bounds, instruction);
        ShowInstruction(instruction);
        return true;
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
                SetState("同じ青い枠の場所で、マウスの左ボタンをもう1回すぐに押してください。", speak: true);
                return;
            }
        }

        await CompleteCurrentStepAsync();
    }

    private async void OnObservedKeyReleased(KeyObservation observation)
    {
        if (_planning || _verifyingAction || _currentDecision is null) return;
        if (_currentDecision.Action.Equals("type_text", StringComparison.OrdinalIgnoreCase))
        {
            var expected = string.IsNullOrWhiteSpace(_currentDecision.Key) ? "Enter" : _currentDecision.Key;
            if (MatchesKeySpec(expected, observation)) await CompleteCurrentStepAsync();
        }
        else if (_currentDecision.Action.Equals("press_key", StringComparison.OrdinalIgnoreCase) && MatchesKeySpec(_currentDecision.Key, observation))
        {
            await CompleteCurrentStepAsync();
        }
    }

    private async Task CompleteCurrentStepAsync()
    {
        if (_verifyingAction || _currentDecision is null || _activeRequest is null || _sessionCts is null) return;
        _verifyingAction = true;
        var decision = _currentDecision;
        var targetName = _currentTarget is null ? (decision.Key ?? "キーボード操作") : DisplayName(_currentTarget.Name, _currentTarget.ControlType);

        try
        {
            SetState("操作の結果を確認しています…", speak: false);
            var changed = await WaitForStateTransitionAsync(decision.Action, _stepBaseline, _stepSystemBaseline, _sessionCts.Token);
            if (!changed)
            {
                _consecutiveFailures++;
                _doubleClickCount = 0;
                if (_consecutiveFailures == 1)
                {
                    var retry = decision.Action.Equals("double_click", StringComparison.OrdinalIgnoreCase)
                        ? "まだ画面が変わっていません。同じ場所で、マウスの左ボタンを間をあけずに2回押してください。"
                        : $"まだ画面が変わっていません。もう一度、同じ操作をしてください。{decision.Instruction}";
                    SetState(retry, speak: true);
                    return;
                }

                _history.Add(new GuideHistoryItem(_stepNumber, $"failed_{decision.Action}", targetName, decision.Instruction));
                _currentDecision = null;
                _currentTarget = null;
                _guidedBounds = null;
                _overlay.Hide();
                _keyHint.Hide();
                if (_consecutiveFailures == 2)
                {
                    _forceVisionNext = true;
                    await AdvanceGuideAsync();
                    return;
                }

                WaitForClarification("同じ操作をしても画面が変わりませんでした。今、画面に何が表示されているか短く教えてください。");
                return;
            }

            _consecutiveFailures = 0;
            _history.Add(new GuideHistoryItem(++_stepNumber, decision.Action, targetName, decision.Instruction));
            if (_history.Count > 12) _history.RemoveAt(0);
            _currentDecision = null;
            _currentTarget = null;
            _stepBaseline = [];
            _stepSystemBaseline = null;
            _guidedBounds = null;
            _doubleClickCount = 0;
            _validatedVisionInstruction = null;
            _rejectedVisionTargets = 0;
            _overlay.Hide();
            _keyHint.Hide();
            SetState("画面が変わったことを確認しました。次を確認しています…", speak: false);
            await Task.Delay(250, _sessionCts.Token);
            await AdvanceGuideAsync();
        }
        catch (OperationCanceledException) { }
        finally
        {
            _verifyingAction = false;
        }
    }

    private async Task<bool> WaitForStateTransitionAsync(string action, IReadOnlyList<UiElementCandidate> before, SystemContextSnapshot? systemBefore, CancellationToken cancellationToken)
    {
        var needsStrongChange = action.Equals("double_click", StringComparison.OrdinalIgnoreCase);
        var allowFocusOnly = action.Equals("press_key", StringComparison.OrdinalIgnoreCase) ||
                             (action.Equals("left_click", StringComparison.OrdinalIgnoreCase) &&
                              _currentTarget?.ControlType is "Edit" or "ComboBox");
        await Task.Delay(needsStrongChange ? 1400 : 500, cancellationToken);

        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(7.5))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var after = await _scanner.CaptureCandidatesAsync(420, cancellationToken);
            var systemAfter = _systemContext.Capture();
            if (HasMeaningfulSystemChange(systemBefore, systemAfter)) return true;
            var changed = needsStrongChange ? HasStrongContentChange(before, after) : HasMeaningfulChange(before, after, allowFocusOnly);
            if (changed)
            {
                await Task.Delay(needsStrongChange ? 700 : 400, cancellationToken);
                return true;
            }
            await Task.Delay(350, cancellationToken);
        }
        return false;
    }

    private static bool HasMeaningfulSystemChange(SystemContextSnapshot? before, SystemContextSnapshot after)
    {
        if (before is null) return false;
        if (!before.ForegroundProcess.Equals(after.ForegroundProcess, StringComparison.OrdinalIgnoreCase)) return true;
        var beforeUrl = before.Browser?.Url ?? string.Empty;
        var afterUrl = after.Browser?.Url ?? string.Empty;
        if (!beforeUrl.Equals(afterUrl, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(afterUrl)) return true;
        return false;
    }

    private static bool HasMeaningfulChange(IReadOnlyList<UiElementCandidate> before, IReadOnlyList<UiElementCandidate> after, bool allowFocusOnly)
    {
        if (before.Count == 0) return after.Count > 0;
        if (HasWindowSetChange(before, after)) return true;
        if (allowFocusOnly)
        {
            var beforeFocus = before.FirstOrDefault(x => x.Focused);
            var afterFocus = after.FirstOrDefault(x => x.Focused);
            if (beforeFocus is not null && afterFocus is not null && !StableKey(beforeFocus).Equals(StableKey(afterFocus), StringComparison.Ordinal)) return true;
        }
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

    private static bool MatchesKeySpec(string? keySpec, KeyObservation observation)
    {
        if (string.IsNullOrWhiteSpace(keySpec)) return false;
        var parts = keySpec.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => x.ToLowerInvariant()).ToArray();
        if (parts.Length == 0) return false;
        var needCtrl = parts.Any(x => x is "ctrl" or "control");
        var needAlt = parts.Contains("alt");
        var needShift = parts.Contains("shift");
        var needWindows = parts.Any(x => x is "windows" or "win");
        if (needCtrl && !observation.Control || needAlt && !observation.Alt || needShift && !observation.Shift || needWindows && !observation.Windows) return false;

        var main = parts.LastOrDefault(x => x is not ("ctrl" or "control" or "alt" or "shift" or "windows" or "win"));
        if (main is null) return needWindows && observation.VirtualKey is 0x5B or 0x5C;
        return VirtualKey(main) == observation.VirtualKey;
    }

    private static int VirtualKey(string key) => key switch
    {
        "enter" or "return" => 0x0D,
        "tab" => 0x09,
        "escape" or "esc" => 0x1B,
        "space" => 0x20,
        "delete" or "del" => 0x2E,
        "backspace" => 0x08,
        "left" => 0x25,
        "up" => 0x26,
        "right" => 0x27,
        "down" => 0x28,
        _ when key.Length == 1 && key[0] is >= 'a' and <= 'z' => char.ToUpperInvariant(key[0]),
        _ when key.Length == 1 && char.IsDigit(key[0]) => key[0],
        _ => -1
    };

    private static string DefaultInstruction(string action) => action.ToLowerInvariant() switch
    {
        "double_click" => "青い枠で囲まれた場所で、マウスの左ボタンを間をあけずに2回押してください。",
        "type_text" => "青い枠の入力欄に文字を入力し、最後に「Enter」と書かれたキーを1回押してください。",
        "press_key" => "画面に表示されたキーボードのキーを押してください。",
        _ => "青い枠で囲まれた場所で、マウスの左ボタンを1回押してください。"
    };

    private static string DisplayName(string name, string controlType) => string.IsNullOrWhiteSpace(name) ? controlType.Replace("ControlType.", string.Empty) : name;

    private void ShowInstruction(string instruction)
    {
        _lastInstruction = instruction;
        SetState($"手順 {_stepNumber + 1}：{instruction}", speak: false);
        _speechOutput.Speak(instruction);
    }

    private void WaitForClarification(string question)
    {
        _overlay.Hide();
        _keyHint.Hide();
        _actionObserver.Stop();
        _currentDecision = null;
        _currentTarget = null;
        _guidedBounds = null;
        _awaitingClarification = true;
        _clarificationQuestion = question;
        GuideButton.Content = "答える";
        RequestBox.Clear();
        RequestBox.Focus();
        _lastInstruction = question;
        SetState($"確認：{question}", speak: false);
        _speechOutput.Speak(question);
    }

    private void SetState(string message, bool speak)
    {
        StateText.Text = message;
        if (speak)
        {
            _lastInstruction = message;
            _speechOutput.Speak(message);
        }
    }

    private void StopWithMessage(string message)
    {
        _overlay.Hide();
        _keyHint.Hide();
        _currentDecision = null;
        _currentTarget = null;
        _stepBaseline = [];
        _stepSystemBaseline = null;
        _guidedBounds = null;
        _doubleClickCount = 0;
        _verifyingAction = false;
        _actionObserver.Stop();
        _sessionCts?.Cancel();
        _activeRequest = null;
        _awaitingClarification = false;
        _clarificationQuestion = null;
        GuideButton.Content = "案内";
        _lastInstruction = message;
        SetState(message, speak: false);
        _speechOutput.Speak(message);
    }

    private void EndSession()
    {
        _overlay.Hide();
        _keyHint.Hide();
        _speechOutput.Stop();
        _currentDecision = null;
        _currentTarget = null;
        _stepBaseline = [];
        _stepSystemBaseline = null;
        _guidedBounds = null;
        _doubleClickCount = 0;
        _consecutiveFailures = 0;
        _verifyingAction = false;
        _forceVisionNext = false;
        _awaitingClarification = false;
        _clarificationQuestion = null;
        _actionObserver.Stop();
        _sessionCts?.Cancel();
        _sessionCts?.Dispose();
        _sessionCts = null;
        _activeRequest = null;
        _originalRequest = null;
        _history.Clear();
        // Do not force _planning=false here. If an old async planner is still unwinding after
        // Clear/close, keeping this guard true prevents a new session from overlapping it.
        GuideButton.Content = "案内";
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        EndSession();
        RequestBox.Clear();
        SetState("案内を消しました。次にやりたいことを入力してください。", speak: false);
    }

    private void ExitButton_Click(object sender, RoutedEventArgs e) => Close();

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _voiceCts?.Cancel();
        _voiceCts?.Dispose();
        _voiceCts = null;
        EndSession();
        _speechOutput.Dispose();
        _speechInput.Dispose();
        _actionObserver.Dispose();
        _cloudGuide.Dispose();
        _hotKey.Dispose();
        _overlay.Close();
        _keyHint.Close();
    }
}

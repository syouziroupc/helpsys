using System.ComponentModel;
using System.Diagnostics;
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
    private readonly GuidanceSessionController _sessionState = new();
    private readonly DiagnosticModePolicy _diagnosticMode = new();
    private readonly ObservationBroker _observationBroker;
    private string _lastObservationFingerprint = string.Empty;

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
    private int _technicalClarificationRetries;
    private DateTime _lastGuidedClickUtc = DateTime.MinValue;
    private bool _forceVisionNext;

    private bool _planning => _sessionState.PlannerInFlight;
    private bool _verifyingAction => _sessionState.State == GuidanceSessionState.Verifying;
    private bool _awaitingClarification => _sessionState.State == GuidanceSessionState.Clarifying;

    public MainWindow()
    {
        _observationBroker = new ObservationBroker(_systemContext, _scanner);
        InitializeComponent();
        Loaded += OnLoaded;
        SourceInitialized += OnSourceInitialized;
        Closing += OnClosing;
        _actionObserver.LeftClick += OnObservedLeftClickV3;
        _actionObserver.KeyReleased += OnObservedKeyReleasedV3;
        ApplyAiProviderSelection(Environment.GetEnvironmentVariable("HELPSYS_OUTLAW_AI_PROVIDER") ?? "auto");
    }

    private void AiProviderBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (AiProviderBox?.SelectedItem is not System.Windows.Controls.ComboBoxItem item) return;
        ApplyAiProviderSelection(item.Tag?.ToString() ?? "auto");
    }

    private void ApplyAiProviderSelection(string provider)
    {
        provider = provider.ToLowerInvariant() switch
        {
            "gemini" => "gemini",
            "glm" => "glm",
            _ => "auto"
        };

        Environment.SetEnvironmentVariable("HELPSYS_OUTLAW_AI_PROVIDER", provider, EnvironmentVariableTarget.Process);

        if (AiProviderBox is not null)
        {
            foreach (var value in AiProviderBox.Items.OfType<System.Windows.Controls.ComboBoxItem>())
                if (string.Equals(value.Tag?.ToString(), provider, StringComparison.OrdinalIgnoreCase))
                {
                    AiProviderBox.SelectedItem = value;
                    break;
                }
        }

        LocalLogService.Write("ai_provider", provider);
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
        if (_planning || _verifyingAction) return;

        _voiceCts?.Cancel();
        var text = RequestBox.Text.Trim();
        if (text.Length == 0)
        {
            SetState(_awaitingClarification ? (_clarificationQuestion ?? "質問への答えを入力してください。") : "やりたいことを入力してください。", speak: true);
            return;
        }

        LocalLogService.Write(_awaitingClarification ? "clarification_input" : "request", text);

        // The Guide click itself brings HelpSys to the foreground. Freeze the most recently
        // sampled external foreground window here so the snapshot broker observes the user's
        // actual work surface rather than HelpSys.
        _systemContext.CommitStableForegroundForAssistantInteraction();

        if (_awaitingClarification && _activeRequest is not null && _sessionCts is not null)
        {
            var localChoice = await TryHandleLocalAccountChoiceAnswerAsync(text);
            if (localChoice == LocalChoiceAnswerResult.Handled) return;

            _history.Add(new GuideHistoryItem(_stepNumber, "clarification_answer", text, _clarificationQuestion ?? "確認質問"));
            _activeRequest += $"\n利用者からの追加回答: {text}";
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
        _technicalClarificationRetries = 0;
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

    private async Task AdvanceGuideLegacyAsync()
    {
        if (_activeRequest is null || _sessionCts is null || _sessionCts.IsCancellationRequested) return;
        if (!_sessionState.TryBeginOperation(out var generation, GuidanceSessionState.Capturing)) return;
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
        planningCts.CancelAfter(TimeSpan.FromSeconds(40));
        var cancellationToken = planningCts.Token;

        try
        {
            SetState("今の画面と、開いているアプリを確認しています…", speak: false);
            var systemContext = _systemContext.Capture();
            if (!HasUsableForeground(systemContext))
            {
                await Task.Delay(180, cancellationToken);
                if (!_sessionState.IsCurrent(generation)) return;
                systemContext = _systemContext.Capture();
            }

            if (!HasUsableForeground(systemContext))
            {
                if (_sessionState.IsCurrent(generation))
                    HandleTechnicalPlanningUncertainty("前面ウィンドウを特定できない", generation);
                return;
            }

            await _liveWatcher.SetForegroundProcessAsync(systemContext.ForegroundProcessId, cancellationToken);
            var candidates = await _scanner.CaptureCandidatesForProcessAsync(systemContext.ForegroundProcessId, 420, cancellationToken);
            if (!_sessionState.IsCurrent(generation)) return;

            if (!_sessionState.TryTransition(generation, GuidanceSessionState.Planning)) return;

            if (_forceVisionNext)
            {
                _forceVisionNext = false;
                if (!await TryVisionFallbackAsync(candidates, systemContext, generation, cancellationToken) && _sessionState.IsCurrent(generation))
                    WaitForClarification("操作のあとで画面を確認できませんでした。今、画面に何が表示されているか短く教えてください。", generation);
                return;
            }

            if (candidates.Count == 0)
            {
                if (!await TryVisionFallbackAsync(candidates, systemContext, generation, cancellationToken) && _sessionState.IsCurrent(generation))
                    HandleTechnicalPlanningUncertainty("現在画面から次の操作候補を確定できない", generation);
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
            catch (GuideServiceException error)
            {
                if (_sessionState.IsCurrent(generation)) StopWithGuideFailure(error);
                return;
            }

            if (!_sessionState.IsCurrent(generation)) return;

            if (decision.Status.Equals("done", StringComparison.OrdinalIgnoreCase))
            {
                StopWithMessage(string.IsNullOrWhiteSpace(decision.Instruction) ? "目的の画面まで進めました。" : decision.Instruction);
                return;
            }

            if (decision.Status.Equals("clarify", StringComparison.OrdinalIgnoreCase))
            {
                WaitForClarification(decision.Question ?? "どれを使いたいか教えてください。", generation);
                return;
            }

            if (!decision.Status.Equals("target", StringComparison.OrdinalIgnoreCase) || decision.Confidence < MinimumTargetConfidence)
            {
                if (!await TryVisionFallbackAsync(candidates, systemContext, generation, cancellationToken) && _sessionState.IsCurrent(generation))
                    HandleTechnicalPlanningUncertainty("案内判断の信頼度が不足している", generation);
                return;
            }

            if (decision.Action.Equals("press_key", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(decision.TargetId))
            {
                ShowKeyboardGuide(decision, candidates, systemContext, generation);
                return;
            }

            if (string.IsNullOrWhiteSpace(decision.TargetId))
            {
                if (!await TryVisionFallbackAsync(candidates, systemContext, generation, cancellationToken) && _sessionState.IsCurrent(generation))
                    HandleTechnicalPlanningUncertainty("案内対象IDを確定できない", generation);
                return;
            }

            var target = candidates.FirstOrDefault(x => string.Equals(x.Id, decision.TargetId, StringComparison.Ordinal));
            if (target is null || !target.Interactable || target.Bounds.IsEmpty)
            {
                if (!await TryVisionFallbackAsync(candidates, systemContext, generation, cancellationToken) && _sessionState.IsCurrent(generation))
                    HandleTechnicalPlanningUncertainty("案内対象が現在画面で操作可能ではない", generation);
                return;
            }

            var freshTarget = await _scanner.RevalidateCandidateAsync(target, systemContext.ForegroundProcessId, cancellationToken);
            if (!_sessionState.IsCurrent(generation)) return;
            if (HasSystemTransitionV3(systemContext, _systemContext.Capture()))
            {
                HandleTechnicalPlanningUncertainty("操作中に画面が切り替わった", generation);
                return;
            }
            if (freshTarget is null)
            {
                if (!await TryVisionFallbackAsync(candidates, systemContext, generation, cancellationToken) && _sessionState.IsCurrent(generation))
                    HandleTechnicalPlanningUncertainty("表示直前に操作対象を再確認できない", generation);
                return;
            }

            ShowStructuredTarget(decision, freshTarget, candidates, systemContext, generation);
        }
        catch (OperationCanceledException)
        {
            if (_sessionState.IsCurrent(generation) && _sessionCts is { IsCancellationRequested: false })
                HandleTechnicalPlanningUncertainty("案内処理が工程期限内に完了しない", generation);
        }
        catch (Exception ex)
        {
            if (_sessionState.IsCurrent(generation)) HandleTechnicalPlanningUncertainty($"画面確認例外: {ex.GetType().Name}", generation);
        }
        finally
        {
            _sessionState.EndOperation(generation);
            GuideButton.IsEnabled = !_planning;
        }
    }

    private static bool HasUsableForeground(SystemContextSnapshot context) =>
        context.ForegroundProcessId > 0 && !string.IsNullOrWhiteSpace(context.ForegroundProcess);

    private void ShowStructuredTarget(GuideDecision decision, UiElementCandidate target, IReadOnlyList<UiElementCandidate> candidates, SystemContextSnapshot systemContext, long generation)
    {
        var safeDecision = NormalizeStructuredDecisionForTarget(decision, target);
        if (safeDecision is null)
        {
            HandleTechnicalPlanningUncertainty("操作対象と操作方法の組み合わせを最終確認できない", generation);
            return;
        }
        decision = safeDecision;

        if (!TryAcceptOutlawGuidance(decision, target, candidates, systemContext, target.Bounds, generation)) return;
        if (!_sessionState.TryTransition(generation, GuidanceSessionState.Presenting)) return;
        _technicalClarificationRetries = 0;
        if (!OutlawModePolicy.Enabled)
        {
            ResetCurrentStateReplanBudget();
            ResetResilienceRecovery();
        }
        _currentDecision = decision;
        _currentTarget = target;
        _stepBaseline = candidates;
        _stepSystemBaseline = systemContext;
        _guidedBounds = target.Bounds;
        var instruction = string.IsNullOrWhiteSpace(decision.Instruction) ? DefaultInstruction(decision.Action) : decision.Instruction;
        _overlay.ShowTarget(target.Bounds, instruction);
        if (!_sessionState.TryTransition(generation, GuidanceSessionState.AwaitingUserAction))
        {
            _overlay.Hide();
            return;
        }
        ShowInstruction(instruction);
    }

    private static GuideDecision? NormalizeStructuredDecisionForTarget(GuideDecision decision, UiElementCandidate target)
    {
        var action = (decision.Action ?? string.Empty).Trim().ToLowerInvariant();
        var controlType = (target.ControlType ?? string.Empty).Trim().ToLowerInvariant();

        if (action is "" or "none" or "press_key") return null;

        if (action == "type_text")
        {
            var editable = controlType is "edit" or "document" or "combobox";
            if (!editable || !target.Focused || !target.KeyboardFocusable) return null;
            return decision;
        }

        if (action != "left_click" && action != "double_click") return null;

        var singleClickControl = controlType is
            "button" or "menuitem" or "hyperlink" or "checkbox" or "radiobutton" or "tabitem" or "combobox";
        if (action == "double_click" && singleClickControl)
            action = "left_click";

        var label = string.IsNullOrWhiteSpace(target.Name) ? "場所" : $"「{target.Name.Trim()}」";
        var instruction = action == "double_click"
            ? $"青い枠の{label}で、マウスの左ボタンを間をあけずに2回押してください。"
            : $"青い枠の{label}で、マウスの左ボタンを1回押してください。";

        return new GuideDecision(
            decision.Status,
            decision.TargetId,
            action,
            instruction,
            decision.Question,
            decision.Key,
            decision.Confidence);
    }

    private void ShowKeyboardGuide(GuideDecision decision, IReadOnlyList<UiElementCandidate> candidates, SystemContextSnapshot systemContext, long generation)
    {
        if (!TryAcceptOutlawGuidance(decision, null, candidates, systemContext, null, generation)) return;
        if (!_sessionState.TryTransition(generation, GuidanceSessionState.Presenting)) return;
        _technicalClarificationRetries = 0;
        if (!OutlawModePolicy.Enabled)
        {
            ResetCurrentStateReplanBudget();
            ResetResilienceRecovery();
        }
        _currentDecision = decision;
        _currentTarget = null;
        _stepBaseline = candidates;
        _stepSystemBaseline = systemContext;
        _guidedBounds = null;
        _overlay.Hide();
        var instruction = string.IsNullOrWhiteSpace(decision.Instruction) ? DefaultInstruction("press_key") : decision.Instruction;
        _keyHint.ShowKeys(decision.Key, instruction);
        if (!_sessionState.TryTransition(generation, GuidanceSessionState.AwaitingUserAction))
        {
            _keyHint.Hide();
            return;
        }
        ShowInstruction(instruction);
    }

    private async Task<bool> TryVisionFallbackAsync(IReadOnlyList<UiElementCandidate> candidates, SystemContextSnapshot systemContext, long generation, CancellationToken cancellationToken)
    {
        if (_activeRequest is null || !_sessionState.IsCurrent(generation)) return false;
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
            return false;
        }
        finally
        {
            Opacity = previousOpacity;
        }

        if (!_sessionState.IsCurrent(generation)) return false;

        VisionGuideDecision decision;
        try
        {
            decision = await _cloudGuide.PlanVisionAsync(_activeRequest, frame, _history, systemContext, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (GuideServiceException error)
        {
            if (_sessionState.IsCurrent(generation)) StopWithGuideFailure(error);
            return true;
        }

        if (!_sessionState.IsCurrent(generation)) return false;

        if (decision.Status.Equals("done", StringComparison.OrdinalIgnoreCase))
        {
            StopWithMessage(string.IsNullOrWhiteSpace(decision.Instruction) ? "目的の画面まで進めました。" : decision.Instruction);
            return true;
        }

        if (decision.Status.Equals("clarify", StringComparison.OrdinalIgnoreCase))
        {
            WaitForClarification(decision.Question ?? "どれを使いたいか教えてください。", generation);
            return true;
        }

        if (!decision.Status.Equals("target", StringComparison.OrdinalIgnoreCase) || decision.Confidence < MinimumVisionConfidence) return false;

        var bounds = frame.MapNormalizedBounds(decision.X, decision.Y, decision.Width, decision.Height);
        if (bounds.IsEmpty || bounds.Width < 8 || bounds.Height < 8) return false;
        var snapped = await _scanner.SnapToAccessibleBoundsAsync(bounds, cancellationToken);
        if (!_sessionState.IsCurrent(generation)) return false;
        if (HasSystemTransitionV3(systemContext, _systemContext.Capture()))
        {
            HandleTechnicalPlanningUncertainty("画像確認中に操作対象の画面が切り替わった", generation);
            return true;
        }

        if (snapped is not { } accessible || accessible.IsEmpty)
        {
            _history.Add(new GuideHistoryItem(_stepNumber, "vision_target_rejected", "画像候補", "画像AIの座標に現在押せるWindows要素が無いため、発話前に破棄した。"));
            if (_history.Count > 64) _history.RemoveAt(0);
            return false;
        }
        bounds = accessible;

        var instruction = string.IsNullOrWhiteSpace(decision.Instruction)
            ? "青い枠で囲まれた場所を、マウスの左ボタンで1回押してください。"
            : decision.Instruction;
        if (!_sessionState.TryTransition(generation, GuidanceSessionState.Presenting)) return false;
        _technicalClarificationRetries = 0;
        _currentDecision = new GuideDecision("target", "vision-target", "left_click", instruction, null, null, decision.Confidence);
        _currentTarget = null;
        _stepBaseline = candidates;
        _stepSystemBaseline = systemContext;
        _guidedBounds = bounds;
        _validatedVisionInstruction = null;
        _overlay.ShowTarget(bounds, instruction);
        if (!_sessionState.TryTransition(generation, GuidanceSessionState.AwaitingUserAction))
        {
            _overlay.Hide();
            return false;
        }
        ShowInstruction(instruction);
        return true;
    }

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

    private void WaitForClarification(string question, long? generation = null)
    {
        if (!IsGenuineDecisionClarification(question))
        {
            var effectiveGeneration = generation ?? _sessionState.Generation;
            if (generation.HasValue && !_sessionState.IsCurrent(generation.Value)) return;

            LocalLogService.Write("technical_uncertainty", question);
            HandleTechnicalPlanningUncertainty(question, effectiveGeneration);
            return;
        }

        _technicalClarificationRetries = 0;
        ResetCurrentStateReplanBudget();

        if (generation.HasValue)
        {
            if (!_sessionState.TryTransition(generation.Value, GuidanceSessionState.Clarifying)) return;
        }
        else
        {
            _sessionState.Invalidate(GuidanceSessionState.Clarifying);
        }

        _overlay.Hide();
        _keyHint.Hide();
        _actionObserver.Stop();
        _currentDecision = null;
        _currentTarget = null;
        _guidedBounds = null;
        _clarificationQuestion = question;
        GuideButton.Content = "答える";
        RequestBox.Clear();
        RequestBox.Focus();
        _lastInstruction = question;
        LocalLogService.Write("clarification", question);
        SetState($"確認：{question}", speak: false);
        _speechOutput.Speak(question);
    }

    private static bool IsGenuineDecisionClarification(string question)
    {
        if (string.IsNullOrWhiteSpace(question)) return false;
        var text = question.Trim();
        return text.Contains("アカウント", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("プロフィール", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("プロファイル", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("使う人", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("どの名前", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("上書き", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("置き換え", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("削除", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("保存先", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("購入", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("支払い", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("注文", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("許可", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("既定", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("デフォルト", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("どのファイル", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("どのフォルダー", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("どのプリンター", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("部数", StringComparison.OrdinalIgnoreCase);
    }

    private void SetState(string message, bool speak)
    {
        LocalLogService.Write("state", message);
        StateText.Text = message;
        if (speak)
        {
            _lastInstruction = message;
            _speechOutput.Speak(message);
        }
    }

    private void StopWithGuideFailure(GuideServiceException error)
    {
        if (error.Kind == GuideFailureKind.PrivacyBlocked)
        {
            // PrivacyBlocked already raises PrivacyBlocked and moves the session into resumable
            // privacy pause. Never overwrite that state with a terminal guidance error.
            if (!_privacyPaused)
                SetState("秘密情報を扱う画面のため、外部送信を停止しています。安全な画面に戻ると自動再開します。", speak: false);
            return;
        }

        var reason = error.Kind switch
        {
            GuideFailureKind.Network => "案内サービスへの通信に一時的に失敗",
            GuideFailureKind.ServiceUnavailable => "案内サービスの応答が一時的に利用不可",
            GuideFailureKind.Rejected => "案内サービスが要求を受理できない",
            GuideFailureKind.InvalidResponse => "案内サービスの応答形式を利用できない",
            GuideFailureKind.ContextChanged => "案内処理中に画面が切り替わった",
            _ => "案内サービスの一時的な処理失敗"
        };

        if (!QueueResilientRecovery(reason))
            SetState("案内処理を再開できる状態を確認しています。", speak: false);
    }

    private void StopWithMessage(string message)
    {
        _sessionState.Invalidate(GuidanceSessionState.Stopped);
        _overlay.Hide();
        _keyHint.Hide();
        _currentDecision = null;
        _currentTarget = null;
        _stepBaseline = [];
        _stepSystemBaseline = null;
        _guidedBounds = null;
        _doubleClickCount = 0;
        _actionObserver.Stop();
        _sessionCts?.Cancel();
        _activeRequest = null;
        _clarificationQuestion = null;
        GuideButton.Content = "案内";
        _lastInstruction = message;
        SetState(message, speak: false);
        _speechOutput.Speak(message);
    }

    private void EndSession()
    {
        CancelResilienceRecovery();
        _sessionState.Invalidate(GuidanceSessionState.Idle);
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
        _technicalClarificationRetries = 0;
        ResetCurrentStateReplanBudget();
        ResetOutlawLoopGuard();
        _forceVisionNext = false;
        _clarificationQuestion = null;
        _localChoiceTargetActive = false;
        _actionObserver.Stop();
        _sessionCts?.Cancel();
        _sessionCts?.Dispose();
        _sessionCts = null;
        _activeRequest = null;
        _originalRequest = null;
        _history.Clear();
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
        _systemContext.Dispose();
        _hotKey.Dispose();
        _overlay.Close();
        _keyHint.Close();
    }
}
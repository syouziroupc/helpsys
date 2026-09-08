using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using HelpSys.Models;
using HelpSys.Services;

namespace HelpSys;

public partial class MainWindow
{
    private readonly GuidanceStateWatcher _liveWatcher = new();
    private readonly SemaphoreSlim _liveObserveGate = new(1, 1);
    private IReadOnlyList<UiElementCandidate> _liveElements = [];
    private SystemContextSnapshot? _liveSystem;
    private bool _liveReplanPending;
    private bool _liveRestartAfterPlanCancel;
    private bool _liveWatcherStarted;
    private string? _validatedVisionInstruction;
    private int _rejectedVisionTargets;

    private void MainWindow_LiveLoaded(object sender, RoutedEventArgs e)
    {
        if (_liveWatcherStarted) return;
        _liveWatcherStarted = true;
        _liveWatcher.Pulse += LiveWatcher_Pulse;
        _liveWatcher.Start();
    }

    private void MainWindow_LiveClosing(object? sender, CancelEventArgs e)
    {
        if (!_liveWatcherStarted) return;
        _liveWatcherStarted = false;
        _liveWatcher.Pulse -= LiveWatcher_Pulse;
        _liveWatcher.Dispose();
        _liveObserveGate.Dispose();
    }

    private void LiveWatcher_Pulse(object? sender, EventArgs e)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        _ = Dispatcher.InvokeAsync(ObserveLiveStateAsync);
    }

    private async Task ObserveLiveStateAsync()
    {
        if (_awaitingClarification) EnsureClarificationUi();
        else HideClarificationUiIfNeeded();

        if (_activeRequest is null)
        {
            ResetLiveBaseline();
            return;
        }

        if (_liveRestartAfterPlanCancel && !_planning)
        {
            RestartSessionTokenAfterStalePlan();
            _liveReplanPending = true;
        }

        if (_sessionCts is null || (_sessionCts.IsCancellationRequested && !_liveRestartAfterPlanCancel)) return;
        if (!await _liveObserveGate.WaitAsync(0)) return;

        try
        {
            var token = _sessionCts.IsCancellationRequested ? CancellationToken.None : _sessionCts.Token;
            IReadOnlyList<UiElementCandidate> nowElements;
            try { nowElements = await _scanner.CaptureCandidatesAsync(320, token); }
            catch (OperationCanceledException) { return; }
            var nowSystem = _systemContext.Capture();

            if (_liveSystem is null || _liveElements.Count == 0)
            {
                _liveElements = nowElements;
                _liveSystem = nowSystem;
                await ValidateCurrentVisionTargetAsync(token);
                await TryRunPendingLiveReplanAsync();
                return;
            }

            var changed = HasLiveStateChanged(_liveElements, _liveSystem, nowElements, nowSystem);
            _liveElements = nowElements;
            _liveSystem = nowSystem;

            if (changed)
            {
                _rejectedVisionTargets = 0;
                _validatedVisionInstruction = null;

                // If the instructed action itself is being verified, CompleteCurrentStepAsync owns
                // the transition. Otherwise any user/app change invalidates the old instruction.
                if (!_verifyingAction)
                {
                    var hadInstruction = _currentDecision is not null || _awaitingClarification;
                    if (hadInstruction)
                    {
                        _history.Add(new GuideHistoryItem(_stepNumber, "screen_changed", "現在の画面", "利用者またはアプリによって画面が変わったため、古い案内を破棄して現在状態から再計画する。"));
                        if (_history.Count > 12) _history.RemoveAt(0);
                    }

                    InvalidateCurrentGuidanceForLiveChange();
                    if (_planning)
                    {
                        // The cloud request is based on an obsolete screen. Cancel the linked
                        // session token, wait for the stale plan to unwind, then create a fresh
                        // token without losing the user's goal/history.
                        _liveRestartAfterPlanCancel = true;
                        try { _sessionCts?.Cancel(); } catch { }
                    }
                    _liveReplanPending = true;
                    SetState("画面が変わりました。今見えている画面から案内を作り直しています…", speak: false);
                }
            }

            await ValidateCurrentVisionTargetAsync(token);
            await TryRunPendingLiveReplanAsync();
        }
        finally
        {
            _liveObserveGate.Release();
        }
    }

    private void InvalidateCurrentGuidanceForLiveChange()
    {
        _overlay.Hide();
        _keyHint.Hide();
        _currentDecision = null;
        _currentTarget = null;
        _guidedBounds = null;
        _stepBaseline = [];
        _stepSystemBaseline = null;
        _doubleClickCount = 0;
        _consecutiveFailures = 0;

        if (_awaitingClarification)
        {
            _awaitingClarification = false;
            _clarificationQuestion = null;
            HideClarificationUiIfNeeded(force: true);
        }
    }

    private void RestartSessionTokenAfterStalePlan()
    {
        if (!_liveRestartAfterPlanCancel || _planning || _activeRequest is null) return;
        var old = _sessionCts;
        _sessionCts = new CancellationTokenSource();
        _liveRestartAfterPlanCancel = false;
        try { old?.Dispose(); } catch { }
        try { _actionObserver.Start(); } catch { }
    }

    private async Task TryRunPendingLiveReplanAsync()
    {
        if (!_liveReplanPending || _planning || _verifyingAction || _awaitingClarification || _activeRequest is null) return;
        if (_sessionCts is null) return;
        if (_sessionCts.IsCancellationRequested)
        {
            if (_liveRestartAfterPlanCancel) return;
            return;
        }

        _liveReplanPending = false;
        await Task.Delay(220, _sessionCts.Token);
        await AdvanceGuideAsync();
    }

    private async Task ValidateCurrentVisionTargetAsync(CancellationToken cancellationToken)
    {
        if (_planning || _verifyingAction || _currentDecision is null || _guidedBounds is null) return;
        if (!string.Equals(_currentDecision.TargetId, "vision-target", StringComparison.Ordinal)) return;
        if (string.Equals(_validatedVisionInstruction, _currentDecision.Instruction, StringComparison.Ordinal)) return;

        var bounds = _guidedBounds.Value;
        // The guide overlay is topmost. Hide it before FromPoint-based validation so the
        // scanner sees the real application underneath rather than HelpSys itself.
        _overlay.Hide();
        await Task.Delay(35, cancellationToken);

        Rect? accessible;
        try { accessible = await _scanner.SnapToAccessibleBoundsAsync(bounds, cancellationToken); }
        catch (OperationCanceledException) { return; }

        if (accessible is null || accessible.Value.IsEmpty)
        {
            _rejectedVisionTargets++;
            _history.Add(new GuideHistoryItem(_stepNumber, "vision_target_rejected", "空白または押せない場所", "画像AIの座標に実際の押せるWindows要素が無かったため、この案内は破棄した。"));
            if (_history.Count > 12) _history.RemoveAt(0);
            _currentDecision = null;
            _currentTarget = null;
            _guidedBounds = null;
            _validatedVisionInstruction = null;

            if (_rejectedVisionTargets >= 2)
            {
                WaitForClarification("押せる場所を安全に確認できませんでした。今、画面に何が表示されているか短く教えてください。");
                EnsureClarificationUi();
                return;
            }

            _liveReplanPending = true;
            SetState("青い枠の場所が実際には押せる場所ではなかったため、案内を作り直しています…", speak: false);
            return;
        }

        var snapped = accessible.Value;
        _guidedBounds = snapped;
        _validatedVisionInstruction = _currentDecision.Instruction;
        _overlay.ShowTarget(snapped, _currentDecision.Instruction);
    }

    private static bool HasLiveStateChanged(
        IReadOnlyList<UiElementCandidate> beforeElements,
        SystemContextSnapshot beforeSystem,
        IReadOnlyList<UiElementCandidate> afterElements,
        SystemContextSnapshot afterSystem)
    {
        if (!beforeSystem.ForegroundProcess.Equals(afterSystem.ForegroundProcess, StringComparison.OrdinalIgnoreCase)) return true;
        if (!beforeSystem.ForegroundTitle.Equals(afterSystem.ForegroundTitle, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(afterSystem.ForegroundTitle)) return true;

        var beforeUrl = beforeSystem.Browser?.Url ?? string.Empty;
        var afterUrl = afterSystem.Browser?.Url ?? string.Empty;
        if (!beforeUrl.Equals(afterUrl, StringComparison.OrdinalIgnoreCase) && (!string.IsNullOrWhiteSpace(beforeUrl) || !string.IsNullOrWhiteSpace(afterUrl))) return true;

        var before = RelevantLiveKeys(beforeElements, beforeSystem.ForegroundProcess);
        var after = RelevantLiveKeys(afterElements, afterSystem.ForegroundProcess);
        if (before.Count == 0 || after.Count == 0) return before.Count != after.Count;
        if (Math.Abs(before.Count - after.Count) >= 5) return true;

        var overlap = before.Count(x => after.Contains(x));
        var similarity = overlap / (double)Math.Max(before.Count, after.Count);
        if (similarity < 0.78) return true;

        var beforeFocused = beforeElements.FirstOrDefault(x => x.Focused && IsRelevantProcess(x.ProcessName, beforeSystem.ForegroundProcess));
        var afterFocused = afterElements.FirstOrDefault(x => x.Focused && IsRelevantProcess(x.ProcessName, afterSystem.ForegroundProcess));
        if (beforeFocused is not null && afterFocused is not null &&
            !LiveElementKey(beforeFocused).Equals(LiveElementKey(afterFocused), StringComparison.Ordinal)) return true;

        return false;
    }

    private static HashSet<string> RelevantLiveKeys(IReadOnlyList<UiElementCandidate> elements, string foregroundProcess)
    {
        return elements
            .Where(x => IsRelevantProcess(x.ProcessName, foregroundProcess))
            .Where(x => x.Interactable || x.ControlType is "Window" or "Pane" or "Document" or "Text")
            .Select(LiveElementKey)
            .Take(180)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static bool IsRelevantProcess(string processName, string foregroundProcess)
    {
        if (processName.Equals(foregroundProcess, StringComparison.OrdinalIgnoreCase)) return true;
        if (foregroundProcess.Equals("explorer", StringComparison.OrdinalIgnoreCase) &&
            (processName.Contains("SearchHost", StringComparison.OrdinalIgnoreCase) || processName.Contains("StartMenuExperienceHost", StringComparison.OrdinalIgnoreCase))) return true;
        if ((foregroundProcess.Contains("SearchHost", StringComparison.OrdinalIgnoreCase) || foregroundProcess.Contains("StartMenuExperienceHost", StringComparison.OrdinalIgnoreCase)) &&
            processName.Equals("explorer", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string LiveElementKey(UiElementCandidate x)
    {
        var bx = (int)Math.Round(x.X / 24d);
        var by = (int)Math.Round(x.Y / 24d);
        var bw = (int)Math.Round(x.Width / 24d);
        var bh = (int)Math.Round(x.Height / 24d);
        return $"{x.ProcessName}|{x.ControlType}|{x.Name}|{x.AutomationId}|{bx},{by},{bw},{bh}";
    }

    private void ResetLiveBaseline()
    {
        _liveElements = [];
        _liveSystem = null;
        _liveReplanPending = false;
        _liveRestartAfterPlanCancel = false;
        _validatedVisionInstruction = null;
        _rejectedVisionTargets = 0;
        HideClarificationUiIfNeeded(force: true);
    }

    private void EnsureClarificationUi()
    {
        if (!_awaitingClarification) return;
        var firstShow = ClarificationPanel.Visibility != Visibility.Visible;
        ClarificationQuestionText.Text = _clarificationQuestion ?? "確認したいことがあります。";
        ClarificationPanel.Visibility = Visibility.Visible;
        RequestBox.IsReadOnly = true;
        if (_originalRequest is not null && !RequestBox.Text.Equals(_originalRequest, StringComparison.Ordinal))
        {
            RequestBox.Text = _originalRequest;
            RequestBox.CaretIndex = RequestBox.Text.Length;
        }
        GuideButton.Content = "案内";
        GuideButton.IsEnabled = false;
        VoiceButton.IsEnabled = false;

        if (firstShow)
        {
            AnswerBox.Clear();
            AnswerBox.Focus();
            UpdateLayout();
            PositionNearBottomRight();
        }
    }

    private void HideClarificationUiIfNeeded(bool force = false)
    {
        if (!force && _awaitingClarification) return;
        if (ClarificationPanel.Visibility == Visibility.Collapsed && !RequestBox.IsReadOnly) return;
        ClarificationPanel.Visibility = Visibility.Collapsed;
        RequestBox.IsReadOnly = false;
        VoiceButton.IsEnabled = true;
        if (!_planning) GuideButton.IsEnabled = true;
        GuideButton.Content = "案内";
        AnswerBox.Clear();
        UpdateLayout();
        PositionNearBottomRight();
    }

    private void GuideButton_PreviewMouseLeftButtonDown_Extended(object sender, MouseButtonEventArgs e)
    {
        if (!_awaitingClarification) return;
        e.Handled = true;
        EnsureClarificationUi();
    }

    private void RequestBox_PreviewKeyDown_Extended(object sender, KeyEventArgs e)
    {
        if (!_awaitingClarification || e.Key != Key.Enter) return;
        e.Handled = true;
        EnsureClarificationUi();
        AnswerBox.Focus();
    }

    private async void AnswerButton_Click_Extended(object sender, RoutedEventArgs e) => await SubmitClarificationAnswerAsync();

    private async void AnswerBox_KeyDown_Extended(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await SubmitClarificationAnswerAsync();
    }

    private async Task SubmitClarificationAnswerAsync()
    {
        if (!_awaitingClarification || _activeRequest is null || _sessionCts is null) return;
        var answer = AnswerBox.Text.Trim();
        if (answer.Length == 0)
        {
            SetState("下の回答欄に、質問への答えを入力してください。", speak: true);
            AnswerBox.Focus();
            return;
        }

        _history.Add(new GuideHistoryItem(_stepNumber, "clarification_answer", answer, _clarificationQuestion ?? "確認質問"));
        if (_history.Count > 12) _history.RemoveAt(0);
        _awaitingClarification = false;
        _clarificationQuestion = null;
        HideClarificationUiIfNeeded(force: true);
        try { _actionObserver.Start(); } catch { }
        _liveElements = [];
        _liveSystem = null;
        await AdvanceGuideAsync();
    }

    private async void AnswerVoiceButton_Click_Extended(object sender, RoutedEventArgs e)
    {
        if (!_awaitingClarification) return;
        if (_voiceCts is not null)
        {
            _voiceCts.Cancel();
            return;
        }

        _voiceCts = new CancellationTokenSource();
        AnswerVoiceButton.Content = "停止";
        AnswerButton.IsEnabled = false;
        SetState("質問への答えを聞いています。ゆっくり話してください。", speak: false);
        try
        {
            var text = await _speechInput.RecognizeOnceAsync(_voiceCts.Token);
            if (!string.IsNullOrWhiteSpace(text))
            {
                AnswerBox.Text = text;
                AnswerBox.CaretIndex = AnswerBox.Text.Length;
                SetState("回答を聞き取りました。内容を確認して「回答する」を押してください。", speak: false);
            }
        }
        catch (OperationCanceledException) { }
        catch
        {
            SetState("音声を聞き取れませんでした。文字で入力するか、もう一度試してください。", speak: true);
        }
        finally
        {
            _voiceCts?.Dispose();
            _voiceCts = null;
            AnswerVoiceButton.Content = "音声で回答";
            AnswerButton.IsEnabled = true;
        }
    }
}

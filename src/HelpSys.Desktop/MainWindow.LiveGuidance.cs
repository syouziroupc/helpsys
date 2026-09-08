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
        _clarificationQuestion = null;
        HideClarificationUiIfNeeded(force: true);
    }

    private void RestartSessionTokenAfterStalePlan()
    {
        if (!_liveRestartAfterPlanCancel || _sessionState.PlannerInFlight || _activeRequest is null) return;
        var old = _sessionCts;
        _sessionCts = new CancellationTokenSource();
        _liveRestartAfterPlanCancel = false;
        try { old?.Dispose(); } catch { }
        try { _actionObserver.Start(); } catch { }
    }

    private async Task TryRunPendingLiveReplanAsync()
    {
        if (!_liveReplanPending || _sessionState.PlannerInFlight || _verifyingAction || _awaitingClarification || _activeRequest is null) return;
        if (_sessionCts is null || _sessionCts.IsCancellationRequested) return;

        await Task.Delay(220, _sessionCts.Token);
        if (_sessionState.PlannerInFlight || _verifyingAction || _awaitingClarification ||
            _sessionCts.IsCancellationRequested || _activeRequest is null) return;

        _liveReplanPending = false;
        await AdvanceGuideAsync();
    }

    private async Task ValidateCurrentVisionTargetAsync(CancellationToken cancellationToken)
    {
        if (_sessionState.State != GuidanceSessionState.AwaitingUserAction ||
            _currentDecision is null || _guidedBounds is null) return;
        if (!string.Equals(_currentDecision.TargetId, "vision-target", StringComparison.Ordinal)) return;
        if (string.Equals(_validatedVisionInstruction, _currentDecision.Instruction, StringComparison.Ordinal)) return;

        var generation = _sessionState.Generation;
        var bounds = _guidedBounds.Value;
        _overlay.Hide();
        await Task.Delay(35, cancellationToken);

        Rect? accessible;
        try { accessible = await _scanner.SnapToAccessibleBoundsAsync(bounds, cancellationToken); }
        catch (OperationCanceledException) { return; }

        if (!_sessionState.IsCurrent(generation)) return;

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
                WaitForClarification("押せる場所を安全に確認できませんでした。今、画面に何が表示されているか短く教えてください。", generation);
                EnsureClarificationUi();
                return;
            }

            _sessionState.Invalidate(GuidanceSessionState.Idle);
            _liveReplanPending = true;
            SetState("青い枠の場所が実際には押せる場所ではなかったため、案内を作り直しています…", speak: false);
            return;
        }

        var snapped = accessible.Value;
        _guidedBounds = snapped;
        _validatedVisionInstruction = _currentDecision.Instruction;
        _overlay.ShowTarget(snapped, _currentDecision.Instruction);
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

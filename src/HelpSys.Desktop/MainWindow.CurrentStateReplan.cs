using HelpSys.Models;
using HelpSys.Services;

namespace HelpSys;

public partial class MainWindow
{
    private const int MaximumAutomaticCurrentStateReplans = 2;
    private int _automaticCurrentStateReplans;

    private bool TryQueueCurrentStateReplan(string reason, long generation)
    {
        if (_activeRequest is null ||
            _sessionCts is null ||
            _sessionCts.IsCancellationRequested ||
            !_sessionState.IsCurrent(generation))
            return false;

        if (_automaticCurrentStateReplans >= MaximumAutomaticCurrentStateReplans)
            return false;

        _automaticCurrentStateReplans++;
        var attempt = _automaticCurrentStateReplans;
        _history.Add(new GuideHistoryItem(
            _stepNumber,
            "current_state_replan",
            "現在の画面",
            $"一時的または技術的な画面不確実性を検出したため、復帰経路ではなく現在状態を再取得する ({attempt}/{MaximumAutomaticCurrentStateReplans}): {reason}"));
        if (_history.Count > 12) _history.RemoveAt(0);

        _speechOutput.Stop();
        ClearCurrentGuidanceV3();
        _sessionState.Invalidate(GuidanceSessionState.Idle);
        _liveReplanPending = true;
        SetState("画面状態を取り直して、現在位置から案内を作り直しています…", speak: false);
        Dispatcher.BeginInvoke(new Action(() => _ = RunQueuedCurrentStateReplanAsync(attempt)));
        return true;
    }

    private async Task RunQueuedCurrentStateReplanAsync(int attempt)
    {
        if (_sessionCts is null || _sessionCts.IsCancellationRequested || _activeRequest is null) return;
        try
        {
            // A second observation is intentionally farther apart so animations, browser navigation,
            // dialogs and shell transitions have time to settle without invoking heavy route recovery.
            var delayMs = attempt <= 1 ? 260 : 620;
            await Task.Delay(delayMs, _sessionCts.Token);
            await TryRunPendingLiveReplanAsync();
        }
        catch (OperationCanceledException) { }
    }

    private void ResetCurrentStateReplanBudget() => _automaticCurrentStateReplans = 0;
}

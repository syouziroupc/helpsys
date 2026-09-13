using HelpSys.Models;
using HelpSys.Services;

namespace HelpSys;

public partial class MainWindow
{
    private const int MaximumAutomaticCurrentStateReplans = 1;
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
        _history.Add(new GuideHistoryItem(
            _stepNumber,
            "current_state_replan",
            "現在の画面",
            $"一時的な画面変化を検出したため、復帰経路ではなく現在状態を再取得する: {reason}"));
        if (_history.Count > 12) _history.RemoveAt(0);

        _speechOutput.Stop();
        ClearCurrentGuidanceV3();
        _sessionState.Invalidate(GuidanceSessionState.Idle);
        _liveReplanPending = true;
        SetState("画面が変わったため、今の画面を確認し直しています…", speak: false);
        Dispatcher.BeginInvoke(new Action(() => _ = RunQueuedCurrentStateReplanAsync()));
        return true;
    }

    private async Task RunQueuedCurrentStateReplanAsync()
    {
        if (_sessionCts is null || _sessionCts.IsCancellationRequested || _activeRequest is null) return;
        try
        {
            await Task.Delay(280, _sessionCts.Token);
            await TryRunPendingLiveReplanAsync();
        }
        catch (OperationCanceledException) { }
    }

    private void ResetCurrentStateReplanBudget() => _automaticCurrentStateReplans = 0;
}

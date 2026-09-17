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
            $"実際の画面変動を検出したため、安定後に現在状態を1回だけ再取得する: {reason}"));
        if (_history.Count > 12) _history.RemoveAt(0);

        _speechOutput.Stop();
        ClearCurrentGuidanceV3();
        _sessionState.Invalidate(GuidanceSessionState.Idle);
        _liveReplanPending = true;
        SetState("画面の切り替わりが落ち着くのを確認して、現在位置を1回だけ取り直しています…", speak: false);
        Dispatcher.BeginInvoke(new Action(() => _ = RunQueuedCurrentStateReplanAsync()));
        return true;
    }

    private async Task RunQueuedCurrentStateReplanAsync()
    {
        if (_sessionCts is null || _sessionCts.IsCancellationRequested || _activeRequest is null) return;
        try
        {
            await Task.Delay(300, _sessionCts.Token);
            await TryRunPendingLiveReplanAsync();
        }
        catch (OperationCanceledException) { }
    }

    private void ResetCurrentStateReplanBudget() => _automaticCurrentStateReplans = 0;
}

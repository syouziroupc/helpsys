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
            !_sessionState.IsCurrent(generation) ||
            !ShouldReobserveCurrentState(reason))
            return false;

        if (_automaticCurrentStateReplans >= MaximumAutomaticCurrentStateReplans)
            return false;

        _automaticCurrentStateReplans++;
        _history.Add(new GuideHistoryItem(
            _stepNumber,
            "current_state_replan",
            "現在の画面",
            $"実際の画面遷移が疑われるため、短時間だけ現在状態を再取得する: {reason}"));
        if (_history.Count > 12) _history.RemoveAt(0);

        _speechOutput.Stop();
        ClearCurrentGuidanceV3();
        _sessionState.Invalidate(GuidanceSessionState.Idle);
        _liveReplanPending = true;
        SetState("画面の切り替わりを1回だけ確認して、現在位置から案内を続けます…", speak: false);
        Dispatcher.BeginInvoke(new Action(() => _ = RunQueuedCurrentStateReplanAsync()));
        return true;
    }

    private async Task RunQueuedCurrentStateReplanAsync()
    {
        if (_sessionCts is null || _sessionCts.IsCancellationRequested || _activeRequest is null) return;
        try
        {
            await Task.Delay(320, _sessionCts.Token);
            await TryRunPendingLiveReplanAsync();
        }
        catch (OperationCanceledException) { }
    }

    private static bool ShouldReobserveCurrentState(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return false;
        string[] transientMarkers =
        [
            "前面ウィンドウ",
            "画面切替",
            "画面変化",
            "画面状態が変化",
            "案内表示直前の画面変化",
            "画像候補確認中の画面変化"
        ];
        return transientMarkers.Any(marker => reason.Contains(marker, StringComparison.Ordinal));
    }

    private void ResetCurrentStateReplanBudget() => _automaticCurrentStateReplans = 0;
}

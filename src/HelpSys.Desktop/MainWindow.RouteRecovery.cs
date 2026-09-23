using HelpSys.Models;
using HelpSys.Services;

namespace HelpSys;

public partial class MainWindow
{
    // Recovery no longer owns a separate planner. It records why the previous action/path failed,
    // invalidates stale guidance, and re-enters the single normal planner with one fresh snapshot.
    private async Task<bool> TryRouteRecoveryAsync(
        string routeIssue,
        long generation,
        CancellationToken cancellationToken)
    {
        if (_activeRequest is null || !_sessionState.IsCurrent(generation)) return false;

        var issue = string.IsNullOrWhiteSpace(routeIssue)
            ? "現在の状態が想定経路と一致しない"
            : routeIssue.Trim();

        _history.Add(new GuideHistoryItem(
            _stepNumber,
            "route_replan",
            "現在の画面",
            $"同じ操作や別Plannerを反復せず、現在状態から通常Plannerを1回だけやり直す: {issue}"));
        if (_history.Count > 64) _history.RemoveAt(0);

        _speechOutput.Stop();
        ClearCurrentGuidanceV3();
        _sessionState.Invalidate(GuidanceSessionState.Idle);
        _liveReplanPending = false;
        SetState("現在位置を取り直し、同じPlannerで次の1手を決め直しています…", speak: false);

        cancellationToken.ThrowIfCancellationRequested();
        await Task.Yield();
        if (_activeRequest is null || _sessionCts is null || _sessionCts.IsCancellationRequested) return false;

        await AdvanceGuideAsync();
        return true;
    }
}

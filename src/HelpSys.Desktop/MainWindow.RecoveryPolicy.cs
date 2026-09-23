using HelpSys.Models;
using HelpSys.Services;

namespace HelpSys;

public partial class MainWindow
{
    private void HandleTechnicalPlanningUncertainty(string reason, long generation)
    {
        if (_activeRequest is null ||
            _sessionCts is null ||
            _sessionCts.IsCancellationRequested ||
            !_sessionState.IsCurrent(generation))
            return;

        if (TryQueueCurrentStateReplan(reason, generation)) return;

        _history.Add(new GuideHistoryItem(
            _stepNumber,
            "automatic_replan_exhausted",
            "現在の画面",
            $"同一画面での自動再計画は1回で打ち切る: {reason}"));
        if (_history.Count > 12) _history.RemoveAt(0);

        _liveReplanPending = false;
        LocalLogService.Write("replan_stop", reason);
        SetState("同じ画面での自動再計画は1回で打ち切りました。画面が変化したら現在位置から再判定します。", speak: false);
    }
}

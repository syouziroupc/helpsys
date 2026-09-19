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
            "technical_planning_uncertainty",
            "現在の画面",
            $"現在状態の再取得を上限まで行ったが安全に確定できなかった。経路逸脱とは推測しない: {reason}"));
        if (_history.Count > 12) _history.RemoveAt(0);

        // Technical observer/planner uncertainty is never a reason to discard the user's goal.
        // After the fast current-state budget is exhausted, move to the backed-off resilience
        // supervisor. Security remains fail-closed at every egress boundary, but guidance recovers
        // automatically when the desktop becomes observable again.
        QueueResilientRecovery(reason, generation);
    }
}

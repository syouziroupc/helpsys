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
            $"経路逸脱とは判定せず、通常の画面再取得で処理する: {reason}"));
        if (_history.Count > 12) _history.RemoveAt(0);

        WaitForClarification(
            "現在の画面を安全に自動判定できないため、画面情報を取り直して案内を続けます。",
            generation);
    }
}

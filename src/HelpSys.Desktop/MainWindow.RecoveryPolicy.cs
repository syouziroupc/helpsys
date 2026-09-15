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

        // Technical observer failure is not a question for the user and must never be converted into
        // a clarification answer that is then appended to the cloud-bound request. Fail closed after
        // bounded current-state replanning and let the user explicitly restart once the UI settles.
#if HELPSYS_TEST_BUILD
        StopWithMessage(
            $"現在の画面を安全に自動判定できませんでした。[TEST:{reason}]");
#else
        StopWithMessage(
            "現在の画面を安全に自動判定できませんでした。画面の切り替えや読み込みが落ち着いてから、もう一度「案内」を押してください。");
#endif
    }
}

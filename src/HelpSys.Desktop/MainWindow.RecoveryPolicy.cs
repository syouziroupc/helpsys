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

        if (!OutlawModePolicy.Enabled && TryQueueCurrentStateReplan(reason, generation)) return;

        _history.Add(new GuideHistoryItem(
            _stepNumber,
            OutlawModePolicy.Enabled ? "one_shot_planning_failed" : "automatic_replan_exhausted",
            "現在の画面",
            OutlawModePolicy.Enabled
                ? $"初回の単一観測・単一Plannerで次の操作を確定できなかった: {reason}"
                : $"同一画面での自動再計画は1回で打ち切る: {reason}"));
        if (_history.Count > 64) _history.RemoveAt(0);

        _liveReplanPending = false;
        LocalLogService.Write(OutlawModePolicy.Enabled ? "outlaw_one_shot_failed" : "replan_stop", reason);
        SetState(
            OutlawModePolicy.Enabled
                ? "この画面の初回判定で次の操作を確定できませんでした。診断ログを保存しました。"
                : "同じ画面での自動再計画は1回で打ち切りました。画面が変化したら現在位置から再判定します。",
            speak: false);
    }
}

using HelpSys.Models;
using HelpSys.Services;

namespace HelpSys;

public partial class MainWindow
{
    private int _technicalRecoveryBusy;

    private void HandleTechnicalPlanningUncertainty(string reason, long generation)
    {
        if (_activeRequest is null ||
            _sessionCts is null ||
            _sessionCts.IsCancellationRequested ||
            !_sessionState.IsCurrent(generation))
            return;

        // Re-observe only when the screen itself may still be moving. Model/evidence uncertainty is
        // not improved by capturing the same unchanged screen over and over.
        if (TryQueueCurrentStateReplan(reason, generation)) return;

        _history.Add(new GuideHistoryItem(
            _stepNumber,
            "technical_planning_uncertainty",
            "現在の画面",
            $"通常判定では確定できなかったため、判定経路を切り替える: {reason}"));
        if (_history.Count > 12) _history.RemoveAt(0);

        QueueAlternateJudgment(reason, generation);
    }

    private void QueueAlternateJudgment(string reason, long generation)
    {
        if (Interlocked.Exchange(ref _technicalRecoveryBusy, 1) != 0) return;

        try
        {
            Dispatcher.BeginInvoke(new Action(async () =>
            {
                try
                {
                    if (_activeRequest is null ||
                        _sessionCts is null ||
                        _sessionCts.IsCancellationRequested ||
                        !_sessionState.IsCurrent(generation) ||
                        _privacyPaused)
                        return;

                    SetState("通常判定で確定できなかったため、別の判定経路で現在画面を解析しています…", speak: false);
                    await TryRouteRecoveryAsync(reason, generation, _sessionCts.Token);
                }
                catch (OperationCanceledException) { }
                catch
                {
                    if (_activeRequest is null ||
                        _sessionCts is null ||
                        _sessionCts.IsCancellationRequested ||
                        !_sessionState.IsCurrent(generation) ||
                        _privacyPaused)
                        return;

                    WaitForClarification(
                        "自動判定を完了できませんでした。今いちばん手前に出ている画面の大きな見出しか、目立つボタン名を1つだけ教えてください。現在位置から案内を続けます。",
                        generation);
                }
                finally
                {
                    Interlocked.Exchange(ref _technicalRecoveryBusy, 0);
                }
            }));
        }
        catch
        {
            Interlocked.Exchange(ref _technicalRecoveryBusy, 0);
        }
    }
}

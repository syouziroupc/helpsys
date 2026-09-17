using System.Windows;
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

        if (TryQueueCurrentStateReplan(reason, generation)) return;

        _history.Add(new GuideHistoryItem(
            _stepNumber,
            "technical_planning_uncertainty",
            "現在の画面",
            $"通常の現在状態再取得だけでは確定できなかったため、別経路で画面を再解析する: {reason}"));
        if (_history.Count > 12) _history.RemoveAt(0);

        QueueTechnicalRecovery(reason, generation);
    }

    private void QueueTechnicalRecovery(string reason, long generation)
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

                    SetState(
                        "通常の画面判定で確定できなかったため、画像と画面構造を使って別経路から案内を続けています…",
                        speak: false);

                    var recovered = await TryRouteRecoveryAsync(reason, generation, _sessionCts.Token);
                    if (recovered ||
                        _privacyPaused ||
                        _sessionCts.IsCancellationRequested ||
                        !_sessionState.IsCurrent(generation))
                        return;

                    WaitForClarification(
                        "自動認識だけでは現在位置を1つに絞れませんでした。今いちばん手前に出ている画面の大きな見出しか、目立つボタン名を1つだけ教えてください。そこで案内を止めずに続けます。",
                        generation);
                }
                catch (OperationCanceledException)
                {
                }
                catch
                {
                    if (_activeRequest is null ||
                        _sessionCts is null ||
                        _sessionCts.IsCancellationRequested ||
                        !_sessionState.IsCurrent(generation) ||
                        _privacyPaused)
                        return;

                    WaitForClarification(
                        "画面の自動再解析が完了しませんでした。今いちばん手前に出ている画面の大きな見出しか、目立つボタン名を1つだけ教えてください。現在位置から案内を続けます。",
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

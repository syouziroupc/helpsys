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

        // Re-observation only helps when the observed Windows state itself is moving. A model
        // uncertainty on an unchanged screen must go to the stronger Gemini recovery path instead
        // of repeatedly scanning/capturing the same evidence.
        if (ShouldReobserveCurrentState(reason) && TryQueueCurrentStateReplan(reason, generation)) return;

        _history.Add(new GuideHistoryItem(
            _stepNumber,
            "technical_planning_uncertainty",
            "現在の画面",
            $"現在の証拠を同じまま再取得せず、Geminiの統合判定で別経路を解析する: {reason}"));
        if (_history.Count > 12) _history.RemoveAt(0);

        QueueTechnicalRecovery(reason, generation);
    }

    private static bool ShouldReobserveCurrentState(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return false;
        string[] volatileReasons =
        [
            "前面ウィンドウを特定できない",
            "画面切替",
            "画面状態が変化",
            "画面変化",
            "表示直前の画面変化",
            "操作対象ウィンドウが変わった",
            "ContextChanged"
        ];
        return volatileReasons.Any(term => reason.Contains(term, StringComparison.OrdinalIgnoreCase));
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
                        "現在の画面情報をGeminiで統合し直して、次の操作を決めています…",
                        speak: false);

                    var recovered = await TryRouteRecoveryAsync(reason, generation, _sessionCts.Token);
                    if (recovered ||
                        _privacyPaused ||
                        _sessionCts.IsCancellationRequested ||
                        !_sessionState.IsCurrent(generation))
                        return;

                    WaitForClarification(
                        "現在の画面だけでは操作対象を一意に決められませんでした。今いちばん手前に出ている画面の大きな見出しか、目立つボタン名を1つだけ教えてください。",
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
                        "画面判定サービスの処理を完了できませんでした。今いちばん手前に出ている画面の大きな見出しか、目立つボタン名を1つだけ教えてください。",
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

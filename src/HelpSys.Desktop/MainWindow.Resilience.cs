using HelpSys.Services;

namespace HelpSys;

public partial class MainWindow
{
    private static readonly int[] ResilienceRecoveryDelaysMs = [180, 420, 900, 1800, 3500, 7000, 12000, 15000];

    private int _resilienceRecoveryStreak;
    private int _resilienceRecoveryQueued;
    private CancellationTokenSource? _resilienceRecoveryCts;
    private string _resilienceLastReason = string.Empty;

    private bool QueueResilientRecovery(string reason, long? generation = null)
    {
        if (_activeRequest is null || _sessionCts is null) return false;
        if (generation.HasValue && !_sessionState.IsCurrent(generation.Value)) return false;
        if (_privacyPaused || _cloudGuide.PrivacyGate.ManualPause) return true;

        _resilienceLastReason = reason;
        _resilienceRecoveryStreak = Math.Min(_resilienceRecoveryStreak + 1, 1024);

        _speechOutput.Stop();
        ClearCurrentGuidanceV3();
        _liveReplanPending = false;
        _sessionState.Invalidate(GuidanceSessionState.Idle);

        var delayIndex = Math.Min(_resilienceRecoveryStreak - 1, ResilienceRecoveryDelaysMs.Length - 1);
        var delayMs = ResilienceRecoveryDelaysMs[delayIndex];
        SetState(
            _resilienceRecoveryStreak <= 2
                ? "一時的に画面を確定できなかったため、現在の状態を取り直しています…"
                : "案内処理を自動復旧しています。目的は保持したまま現在の画面から再開します…",
            speak: false);

        if (Interlocked.Exchange(ref _resilienceRecoveryQueued, 1) != 0) return true;

        var nextCts = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _resilienceRecoveryCts, nextCts);
        try { previous?.Cancel(); } catch { }
        try { previous?.Dispose(); } catch { }

        Dispatcher.BeginInvoke(new Action(() => _ = RunResilientRecoveryAsync(delayMs, nextCts)));
        return true;
    }

    private async Task RunResilientRecoveryAsync(int delayMs, CancellationTokenSource recoveryCts)
    {
        try
        {
            await Task.Delay(delayMs, recoveryCts.Token);
            if (recoveryCts.IsCancellationRequested ||
                _activeRequest is null ||
                _privacyPaused ||
                _cloudGuide.PrivacyGate.ManualPause)
                return;

            // A recovery queued from inside AdvanceGuideAsync must wait until that operation has
            // unwound its finally block. Never start two planners against one desktop generation.
            for (var i = 0; i < 60 && _sessionState.PlannerInFlight; i++)
                await Task.Delay(100, recoveryCts.Token);

            if (_sessionState.PlannerInFlight)
            {
                ReleaseRecoverySlot(recoveryCts);
                QueueResilientRecovery("前の案内処理が終了しないため、復旧を再スケジュール");
                return;
            }

            if (_liveRestartAfterPlanCancel) RestartSessionTokenAfterStalePlan();

            if (_sessionCts is null || _sessionCts.IsCancellationRequested)
            {
                var old = _sessionCts;
                _sessionCts = new CancellationTokenSource();
                try { old?.Dispose(); } catch { }
            }

            ResetCurrentStateReplanBudget();
            _technicalClarificationRetries = 0;
            _liveElements = [];
            _liveSystem = null;
            _liveReplanPending = true;
            SetState("現在の画面を再取得して案内を続けています…", speak: false);

            // Release before re-entering the planner so a failure in the replacement operation can
            // schedule its own bounded/backed-off recovery instead of being swallowed by this run.
            ReleaseRecoverySlot(recoveryCts);
            await TryRunPendingLiveReplanAsync();
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch
        {
            ReleaseRecoverySlot(recoveryCts);
            if (_activeRequest is not null && !_privacyPaused)
                QueueResilientRecovery($"復旧処理自体を再試行: {_resilienceLastReason}");
        }
        finally
        {
            ReleaseRecoverySlot(recoveryCts);
        }
    }

    private void ReleaseRecoverySlot(CancellationTokenSource recoveryCts)
    {
        if (ReferenceEquals(_resilienceRecoveryCts, recoveryCts))
        {
            Interlocked.CompareExchange(ref _resilienceRecoveryCts, null, recoveryCts);
            Interlocked.Exchange(ref _resilienceRecoveryQueued, 0);
            try { recoveryCts.Dispose(); } catch { }
        }
    }

    private void ResetResilienceRecovery()
    {
        _resilienceRecoveryStreak = 0;
        _resilienceLastReason = string.Empty;
        CancelResilienceRecovery();
    }

    private void CancelResilienceRecovery()
    {
        var cts = Interlocked.Exchange(ref _resilienceRecoveryCts, null);
        Interlocked.Exchange(ref _resilienceRecoveryQueued, 0);
        try { cts?.Cancel(); } catch { }
        try { cts?.Dispose(); } catch { }
    }
}

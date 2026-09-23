using HelpSys.Services;

namespace HelpSys;

public partial class MainWindow
{
    private static readonly int[] ResilienceRecoveryDelaysMs = [180, 650, 1500];
    private const int MaximumAutomaticRecoveriesPerState = 3;

    private int _resilienceRecoveryStreak;
    private int _resilienceRecoveryQueued;
    private CancellationTokenSource? _resilienceRecoveryCts;
    private string _resilienceLastReason = string.Empty;
    private string _resilienceFailureKey = string.Empty;

    private bool QueueResilientRecovery(string reason, long? generation = null)
    {
        if (_activeRequest is null || _sessionCts is null) return false;
        if (generation.HasValue && !_sessionState.IsCurrent(generation.Value)) return false;
        if (_privacyPaused || _cloudGuide.PrivacyGate.ManualPause) return true;

        var failureClass = ClassifyRecoveryReason(reason);
        var current = _systemContext.Capture();
        var observationKey = string.IsNullOrWhiteSpace(_lastObservationFingerprint)
            ? $"{current.ForegroundProcessId}|{current.ForegroundWindowHandle}|{current.ForegroundProcess}"
            : _lastObservationFingerprint;
        var failureKey = $"{failureClass}|{observationKey}";

        if (!string.Equals(failureKey, _resilienceFailureKey, StringComparison.Ordinal))
        {
            _resilienceFailureKey = failureKey;
            _resilienceRecoveryStreak = 0;
        }

        _resilienceLastReason = reason;
        _resilienceRecoveryStreak++;

        if (_resilienceRecoveryStreak > MaximumAutomaticRecoveriesPerState)
        {
            CancelResilienceRecovery();
            _liveReplanPending = false;
            var diagnosticSuffix = IsLocalSmokeEndpoint()
                ? $" [reason={_resilienceLastReason}]"
                : string.Empty;
            SetState(
                "同じ画面状態で同じ復旧を繰り返さないため、自動再試行を停止しました。画面が変わるか次の操作が行われると再判定します。" + diagnosticSuffix,
                speak: false);
            return true;
        }

        _speechOutput.Stop();
        ClearCurrentGuidanceV3();
        _liveReplanPending = false;
        _sessionState.Invalidate(GuidanceSessionState.Idle);

        var delayMs = ResilienceRecoveryDelaysMs[Math.Min(
            _resilienceRecoveryStreak - 1,
            ResilienceRecoveryDelaysMs.Length - 1)];

        SetState(
            _resilienceRecoveryStreak == 1
                ? "現在の画面を一度だけ取り直しています…"
                : "同じ状態の復旧を確認しています…",
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

            // Never recursively queue recovery because a previous planner failed to unwind.
            for (var i = 0; i < 20 && _sessionState.PlannerInFlight; i++)
                await Task.Delay(100, recoveryCts.Token);

            if (_sessionState.PlannerInFlight)
            {
                SetState(
                    "前の案内処理が終了していないため、重複する自動再試行を開始しません。",
                    speak: false);
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
            SetState("現在の画面から案内を再計算しています…", speak: false);

            ReleaseRecoverySlot(recoveryCts);
            await TryRunPendingLiveReplanAsync();
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch
        {
            SetState(
                $"復旧処理を完了できませんでした。同じ失敗の自動反復は行いません。({_resilienceLastReason})",
                speak: false);
        }
        finally
        {
            ReleaseRecoverySlot(recoveryCts);
        }
    }

    private static bool IsLocalSmokeEndpoint()
    {
        var value = Environment.GetEnvironmentVariable("HELPSYS_API_BASE");
        return value is not null &&
               (value.StartsWith("http://127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("http://localhost", StringComparison.OrdinalIgnoreCase));
    }

    private static string ClassifyRecoveryReason(string reason)
    {
        if (reason.Contains("privacy", StringComparison.OrdinalIgnoreCase) ||
            reason.Contains("安全", StringComparison.OrdinalIgnoreCase))
            return "privacy";
        if (reason.Contains("network", StringComparison.OrdinalIgnoreCase) ||
            reason.Contains("通信", StringComparison.OrdinalIgnoreCase) ||
            reason.Contains("API", StringComparison.OrdinalIgnoreCase))
            return "network";
        if (reason.Contains("対象", StringComparison.OrdinalIgnoreCase))
            return "target_stale";
        if (reason.Contains("画面", StringComparison.OrdinalIgnoreCase) ||
            reason.Contains("context", StringComparison.OrdinalIgnoreCase))
            return "context_changed";
        if (reason.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
            reason.Contains("時間", StringComparison.OrdinalIgnoreCase))
            return "timeout";
        return "planner_uncertain";
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
        _resilienceFailureKey = string.Empty;
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

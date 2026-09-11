using System.ComponentModel;
using System.Windows;
using HelpSys.Models;
using HelpSys.Services;

namespace HelpSys;

public partial class MainWindow
{
    private string? _stableLiveChangeSignature;
    private DateTime _stableLiveChangeSinceUtc = DateTime.MinValue;
    private int _stableLiveChangeSamples;
    private int _stablePulseQueued;

    private void MainWindow_StableLoaded(object sender, RoutedEventArgs e)
    {
        AttachDeepAuditGuards();
        if (_liveWatcherStarted) return;
        _liveWatcherStarted = true;
        _liveWatcher.Pulse += StableLiveWatcher_Pulse;

        // The global UI Automation focus hook is useful once guidance is active, but registering it
        // synchronously inside WPF Loaded can delay the first visible frame on slower machines.
        // Queue only the watcher startup behind the initial render. Privacy Sentinel remains active
        // independently, and SetForegroundProcessAsync can still establish a task-specific scope if
        // the user starts guidance before this low-priority callback runs.
        Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.ContextIdle,
            new Action(StartLiveWatcherAfterInitialRender));
    }

    private void StartLiveWatcherAfterInitialRender()
    {
        if (!_liveWatcherStarted || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        try { _liveWatcher.Start(); }
        catch { }
    }

    private void MainWindow_StableClosing(object? sender, CancelEventArgs e)
    {
        DetachDeepAuditGuards();
        if (!_liveWatcherStarted) return;
        _liveWatcherStarted = false;
        _liveWatcher.Pulse -= StableLiveWatcher_Pulse;
        Interlocked.Exchange(ref _stablePulseQueued, 0);
        _liveWatcher.Dispose();
    }

    private void StableLiveWatcher_Pulse(object? sender, EventArgs e)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        // A heartbeat may arrive while the previous UIA scan is still awaiting. Queue at most one
        // dispatcher observation so rapid focus/structure changes cannot flood the WPF UI queue.
        if (Interlocked.Exchange(ref _stablePulseQueued, 1) != 0) return;

        try
        {
            Dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await ObserveStableLiveStateAsync(); }
                catch (OperationCanceledException) { }
                catch (ObjectDisposedException) { }
                catch (Exception)
                {
                    ClearStableLiveChangeCandidate();
                }
                finally
                {
                    Interlocked.Exchange(ref _stablePulseQueued, 0);
                }
            }));
        }
        catch
        {
            Interlocked.Exchange(ref _stablePulseQueued, 0);
        }
    }

    private async Task ObserveStableLiveStateAsync()
    {
        if (_awaitingClarification)
        {
            EnsureClarificationUi();
            ClearStableLiveChangeCandidate();
            return;
        }

        HideClarificationUiIfNeeded();

        if (_activeRequest is null)
        {
            ResetLiveBaseline();
            ClearStableLiveChangeCandidate();
            _ = _liveWatcher.SetForegroundProcessAsync(0);
            return;
        }

        if (_liveRestartAfterPlanCancel && !_sessionState.PlannerInFlight)
        {
            RestartSessionTokenAfterStalePlan();
            _liveReplanPending = true;
        }

        if (_sessionCts is null || (_sessionCts.IsCancellationRequested && !_liveRestartAfterPlanCancel)) return;
        if (!await _liveObserveGate.WaitAsync(0)) return;

        try
        {
            var token = _sessionCts.IsCancellationRequested ? CancellationToken.None : _sessionCts.Token;
            var nowSystem = _systemContext.Capture();
            if (!HasUsableForeground(nowSystem))
            {
                ClearStableLiveChangeCandidate();
                await _liveWatcher.SetForegroundProcessAsync(0, token);
                return;
            }

            await _liveWatcher.SetForegroundProcessAsync(nowSystem.ForegroundProcessId, token);
            IReadOnlyList<UiElementCandidate> nowElements;
            try { nowElements = await _scanner.CaptureCandidatesForProcessAsync(nowSystem.ForegroundProcessId, 320, token); }
            catch (OperationCanceledException) { return; }

            var afterScanSystem = _systemContext.Capture();
            if (HasHardStableLiveChange(nowSystem, afterScanSystem))
            {
                ClearStableLiveChangeCandidate();
                _liveElements = [];
                _liveSystem = null;
                if (!_verifyingAction) InvalidatePlannerForLiveContextChange();
                return;
            }
            nowSystem = afterScanSystem;

            if (_liveSystem is null || _liveElements.Count == 0)
            {
                _liveElements = nowElements;
                _liveSystem = nowSystem;
                ClearStableLiveChangeCandidate();
                await ValidateCurrentVisionTargetAsync(token);
                await TryRunPendingLiveReplanAsync();
                return;
            }

            var hardChange = HasHardStableLiveChange(_liveSystem, nowSystem);
            var semanticChange = HasSemanticLiveStateChanged(_liveElements, _liveSystem, nowElements, nowSystem);
            var meaningful = hardChange || semanticChange;
            if (!meaningful)
            {
                _liveElements = nowElements;
                _liveSystem = nowSystem;
                ClearStableLiveChangeCandidate();
                await ValidateCurrentVisionTargetAsync(token);
                await TryRunPendingLiveReplanAsync();
                return;
            }

            var signature = BuildStableLiveSignature(nowElements, nowSystem);
            var now = DateTime.UtcNow;
            if (!string.Equals(_stableLiveChangeSignature, signature, StringComparison.Ordinal))
            {
                _stableLiveChangeSignature = signature;
                _stableLiveChangeSinceUtc = now;
                _stableLiveChangeSamples = 1;
                return;
            }

            _stableLiveChangeSamples++;
            if (_stableLiveChangeSamples < 2 || now - _stableLiveChangeSinceUtc < TimeSpan.FromMilliseconds(350)) return;

            _liveElements = nowElements;
            _liveSystem = nowSystem;
            ClearStableLiveChangeCandidate();
            if (!_verifyingAction) InvalidatePlannerForLiveContextChange();
            await ValidateCurrentVisionTargetAsync(token);
            await TryRunPendingLiveReplanAsync();
        }
        finally
        {
            _liveObserveGate.Release();
        }
    }

    private void ClearStableLiveChangeCandidate()
    {
        _stableLiveChangeSignature = null;
        _stableLiveChangeSinceUtc = DateTime.MinValue;
        _stableLiveChangeSamples = 0;
    }

    private static string BuildStableLiveSignature(IReadOnlyList<UiElementCandidate> elements, SystemContextSnapshot system)
    {
        var ids = elements
            .Where(x => x.Enabled && !x.Bounds.IsEmpty)
            .Take(24)
            .Select(x => $"{x.AutomationId}|{x.ControlType}|{Math.Round(x.Bounds.Left / 8)}|{Math.Round(x.Bounds.Top / 8)}")
            .OrderBy(x => x, StringComparer.Ordinal);
        return $"{system.ForegroundProcessId}|{system.ForegroundWindowHandle}|{system.ForegroundProcessName}|{system.ForegroundWindowTitle}|{string.Join(';', ids)}";
    }
}

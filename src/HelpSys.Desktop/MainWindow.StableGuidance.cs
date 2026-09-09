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
        _liveWatcher.Start();
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

            if (!hardChange && !_verifyingAction && _currentDecision is not null &&
                _currentDecision.Action.Equals("type_text", StringComparison.OrdinalIgnoreCase))
            {
                _liveElements = nowElements;
                _liveSystem = nowSystem;
                ClearStableLiveChangeCandidate();
                await ValidateCurrentVisionTargetAsync(token);
                return;
            }

            var topologyChange = HasStableLiveTopologyChanged(_liveElements, _liveSystem, nowElements, nowSystem);

            if (!hardChange && !topologyChange)
            {
                _liveElements = nowElements;
                _liveSystem = nowSystem;
                ClearStableLiveChangeCandidate();
                await ValidateCurrentVisionTargetAsync(token);
                await TryRunPendingLiveReplanAsync();
                return;
            }

            if (!hardChange && !ConfirmStableLiveChange(nowElements, nowSystem))
            {
                await ValidateCurrentVisionTargetAsync(token);
                return;
            }

            _liveElements = nowElements;
            _liveSystem = nowSystem;
            ClearStableLiveChangeCandidate();
            _rejectedVisionTargets = 0;
            _validatedVisionInstruction = null;

            if (!_verifyingAction)
            {
                var hadInstruction = _currentDecision is not null || _awaitingClarification;
                if (hadInstruction)
                {
                    _history.Add(new GuideHistoryItem(
                        _stepNumber,
                        "screen_changed",
                        "現在の画面",
                        "一時的な入力変化ではなく、安定した画面遷移を確認したため、古い案内を破棄して現在状態から再計画する。"));
                    if (_history.Count > 12) _history.RemoveAt(0);
                }

                InvalidatePlannerForLiveContextChange();
            }

            await ValidateCurrentVisionTargetAsync(token);
            await TryRunPendingLiveReplanAsync();
        }
        finally
        {
            _liveObserveGate.Release();
        }
    }

    private bool ConfirmStableLiveChange(IReadOnlyList<UiElementCandidate> elements, SystemContextSnapshot system)
    {
        var signature = StableLiveSignature(elements, system);
        var now = DateTime.UtcNow;

        if (!string.Equals(signature, _stableLiveChangeSignature, StringComparison.Ordinal))
        {
            _stableLiveChangeSignature = signature;
            _stableLiveChangeSinceUtc = now;
            _stableLiveChangeSamples = 1;
            return false;
        }

        _stableLiveChangeSamples++;
        return _stableLiveChangeSamples >= 2 && (now - _stableLiveChangeSinceUtc) >= TimeSpan.FromMilliseconds(650);
    }

    private void InvalidatePlannerForLiveContextChange()
    {
        var plannerWasInFlight = _sessionState.PlannerInFlight;
        var hadGuidance = _currentDecision is not null || _awaitingClarification;
        if (!plannerWasInFlight && !hadGuidance && !_liveReplanPending) return;

        _sessionState.Invalidate(GuidanceSessionState.Idle);
        _speechOutput.Stop();
        InvalidateCurrentGuidanceForLiveChange();

        if (plannerWasInFlight)
        {
            _liveRestartAfterPlanCancel = true;
            try { _sessionCts?.Cancel(); } catch { }
        }

        _liveReplanPending = true;
        SetState("操作中の画面が切り替わったため、古い案内を破棄しました。新しい画面が落ち着いてから案内を作り直します…", speak: false);
    }

    private void ClearStableLiveChangeCandidate()
    {
        _stableLiveChangeSignature = null;
        _stableLiveChangeSinceUtc = DateTime.MinValue;
        _stableLiveChangeSamples = 0;
    }

    private static bool HasHardStableLiveChange(SystemContextSnapshot before, SystemContextSnapshot after)
    {
        if (before.ForegroundProcessId > 0 && after.ForegroundProcessId > 0 && before.ForegroundProcessId != after.ForegroundProcessId) return true;
        if (!before.ForegroundProcess.Equals(after.ForegroundProcess, StringComparison.OrdinalIgnoreCase)) return true;

        var beforeUrl = before.Browser?.Url ?? string.Empty;
        var afterUrl = after.Browser?.Url ?? string.Empty;
        return !beforeUrl.Equals(afterUrl, StringComparison.OrdinalIgnoreCase) &&
               (!string.IsNullOrWhiteSpace(beforeUrl) || !string.IsNullOrWhiteSpace(afterUrl));
    }

    private static bool HasStableLiveTopologyChanged(
        IReadOnlyList<UiElementCandidate> beforeElements,
        SystemContextSnapshot beforeSystem,
        IReadOnlyList<UiElementCandidate> afterElements,
        SystemContextSnapshot afterSystem)
    {
        var before = StableRelevantLiveKeys(beforeElements, beforeSystem.ForegroundProcess);
        var after = StableRelevantLiveKeys(afterElements, afterSystem.ForegroundProcess);

        if (before.Count == 0 || after.Count == 0) return before.Count != after.Count;
        if (Math.Abs(before.Count - after.Count) >= 10) return true;

        var overlap = before.Count(x => after.Contains(x));
        var similarity = overlap / (double)Math.Max(before.Count, after.Count);
        return similarity < 0.72;
    }

    private static HashSet<string> StableRelevantLiveKeys(IReadOnlyList<UiElementCandidate> elements, string foregroundProcess)
    {
        return elements
            .Where(x => IsRelevantProcess(x.ProcessName, foregroundProcess))
            .Where(x => x.Interactable || x.ControlType is "Window" or "Pane" or "Document" or "Text")
            .Select(StableLiveElementKey)
            .Take(180)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string StableLiveElementKey(UiElementCandidate x)
    {
        var bx = (int)Math.Round(x.X / 24d);
        var by = (int)Math.Round(x.Y / 24d);
        var bw = (int)Math.Round(x.Width / 24d);
        var bh = (int)Math.Round(x.Height / 24d);
        var stableName = IsStableNamedControl(x.ControlType) ? NormalizeStableName(x.Name) : string.Empty;
        var semanticState = x.Interactable
            ? $"focus={x.Focused};toggle={x.ToggleState ?? string.Empty};selected={x.Selected?.ToString() ?? string.Empty};expand={x.ExpandCollapseState ?? string.Empty}"
            : string.Empty;
        return $"{x.ProcessName}|{x.ControlType}|{x.AutomationId}|{x.ClassName}|{stableName}|{semanticState}|{bx},{by},{bw},{bh}";
    }

    private static bool IsStableNamedControl(string controlType) => controlType.ToLowerInvariant() is
        "button" or "menuitem" or "listitem" or "treeitem" or "tabitem" or "hyperlink" or "checkbox" or "radiobutton";

    private static string NormalizeStableName(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var normalized = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= 72 ? normalized : normalized[..72];
    }

    private static string StableLiveSignature(IReadOnlyList<UiElementCandidate> elements, SystemContextSnapshot system)
    {
        var keys = StableRelevantLiveKeys(elements, system.ForegroundProcess)
            .OrderBy(x => x, StringComparer.Ordinal)
            .Take(120);
        var browser = system.Browser?.Url ?? string.Empty;
        return $"{system.ForegroundProcess.ToLowerInvariant()}|{browser.ToLowerInvariant()}|{string.Join("\n", keys)}";
    }
}

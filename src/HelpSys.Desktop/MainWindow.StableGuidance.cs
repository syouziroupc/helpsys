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
            var windowSetChanged = HasLiveWindowSetChanged(_liveElements, _liveSystem, nowElements, nowSystem);
            var semanticChange = HasSemanticLiveStateChanged(_liveElements, _liveSystem, nowElements, nowSystem);
            var topologyChange = windowSetChanged || semanticChange || HasStableLiveTopologyChanged(_liveElements, _liveSystem, nowElements, nowSystem);

            if (!hardChange && !windowSetChanged && semanticChange && IsExpectedGuidanceSemanticProgress(_liveElements, nowElements))
            {
                // A user following the current instruction is expected to change semantic UI state.
                // Focusing the instructed input, selecting the instructed tab, or toggling the
                // instructed checkbox is progress, not a route deviation. The action observer owns
                // completion verification; the live watcher only refreshes its baseline here.
                _liveElements = nowElements;
                _liveSystem = nowSystem;
                ClearStableLiveChangeCandidate();
                await ValidateCurrentVisionTargetAsync(token);
                return;
            }

            if (!hardChange && !topologyChange)
            {
                _liveElements = nowElements;
                _liveSystem = nowSystem;
                ClearStableLiveChangeCandidate();
                await ValidateCurrentVisionTargetAsync(token);
                await TryRunPendingLiveReplanAsync();
                return;
            }

            if (!hardChange && !windowSetChanged && !semanticChange && !ConfirmStableLiveChange(nowElements, nowSystem))
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
                        windowSetChanged ? "window_set_changed" : semanticChange ? "semantic_state_changed" : "screen_changed",
                        "現在の画面",
                        windowSetChanged
                            ? "同じアプリ内でウィンドウやダイアログの構成が変わったため、古い案内を破棄して現在状態から再計画する。"
                            : semanticChange
                                ? "選択・ON/OFF・展開・フォーカスなどの意味状態が変わったため、古い案内を破棄して現在状態から再計画する。"
                                : "一時的な入力変化ではなく、安定した画面遷移を確認したため、古い案内を破棄して現在状態から再計画する。"));
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

    private bool IsExpectedGuidanceSemanticProgress(
        IReadOnlyList<UiElementCandidate> beforeElements,
        IReadOnlyList<UiElementCandidate> afterElements)
    {
        if (_sessionState.State != GuidanceSessionState.AwaitingUserAction ||
            _currentDecision is null || _currentTarget is null)
            return false;

        var action = _currentDecision.Action;
        if (!action.Equals("type_text", StringComparison.OrdinalIgnoreCase) &&
            !action.Equals("left_click", StringComparison.OrdinalIgnoreCase) &&
            !action.Equals("double_click", StringComparison.OrdinalIgnoreCase))
            return false;

        var before = FindMatchingTargetV3(_currentTarget, beforeElements);
        var after = FindMatchingTargetV3(_currentTarget, afterElements);
        if (before is null || after is null) return false;

        var targetChanged = !string.Equals(SemanticLiveState(before), SemanticLiveState(after), StringComparison.Ordinal);
        if (!targetChanged) return false;

        if (action.Equals("type_text", StringComparison.OrdinalIgnoreCase) && !(!before.Focused && after.Focused))
            return false;

        // Focus naturally transfers from the previously focused control to the instructed target.
        // Do not suppress a replan if any other control changed toggle/selection/expand state at the
        // same time; that would hide a real route deviation behind an expected focus change.
        var beforeMap = SemanticLiveCandidates(beforeElements);
        var afterMap = SemanticLiveCandidates(afterElements);
        var targetIdentity = StableLiveElementIdentity(after);
        foreach (var pair in beforeMap)
        {
            if (!afterMap.TryGetValue(pair.Key, out var current)) continue;
            var previous = pair.Value;
            if (SemanticLiveState(previous).Equals(SemanticLiveState(current), StringComparison.Ordinal)) continue;
            if (pair.Key.Equals(targetIdentity, StringComparison.Ordinal)) continue;
            if (OnlyFocusChanged(previous, current)) continue;
            return false;
        }

        return true;
    }

    private static bool OnlyFocusChanged(UiElementCandidate before, UiElementCandidate after) =>
        before.Focused != after.Focused &&
        string.Equals(before.ToggleState, after.ToggleState, StringComparison.Ordinal) &&
        before.Selected == after.Selected &&
        string.Equals(before.ExpandCollapseState, after.ExpandCollapseState, StringComparison.Ordinal);

    private static Dictionary<string, UiElementCandidate> SemanticLiveCandidates(IReadOnlyList<UiElementCandidate> elements)
    {
        var map = new Dictionary<string, UiElementCandidate>(StringComparer.Ordinal);
        foreach (var item in elements.Where(x => x.Interactable))
        {
            var identity = StableLiveElementIdentity(item);
            map.TryAdd(identity, item);
        }
        return map;
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
        return !string.IsNullOrWhiteSpace(beforeUrl) &&
               !string.IsNullOrWhiteSpace(afterUrl) &&
               !beforeUrl.Equals(afterUrl, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasLiveWindowSetChanged(
        IReadOnlyList<UiElementCandidate> beforeElements,
        SystemContextSnapshot beforeSystem,
        IReadOnlyList<UiElementCandidate> afterElements,
        SystemContextSnapshot afterSystem)
    {
        var before = LiveWindowKeys(beforeElements, beforeSystem.ForegroundProcess);
        var after = LiveWindowKeys(afterElements, afterSystem.ForegroundProcess);
        return !before.SetEquals(after);
    }

    private static HashSet<string> LiveWindowKeys(IReadOnlyList<UiElementCandidate> elements, string foregroundProcess) =>
        elements
            .Where(x => x.ControlType.Equals("Window", StringComparison.OrdinalIgnoreCase) && IsRelevantProcess(x.ProcessName, foregroundProcess))
            .Select(x =>
            {
                var bx = (int)Math.Round(x.X / 24d);
                var by = (int)Math.Round(x.Y / 24d);
                var bw = (int)Math.Round(x.Width / 24d);
                var bh = (int)Math.Round(x.Height / 24d);
                // Window titles are dynamic user/content data (document names, tab titles, subjects).
                // They are not stable window identity and must not turn a harmless title refresh into
                // a modal/window transition. Use process/class/automation identity plus geometry.
                return $"{x.ProcessName}|{x.AutomationId}|{x.ClassName}|{bx},{by},{bw},{bh}";
            })
            .ToHashSet(StringComparer.Ordinal);

    private static bool HasSemanticLiveStateChanged(
        IReadOnlyList<UiElementCandidate> beforeElements,
        SystemContextSnapshot beforeSystem,
        IReadOnlyList<UiElementCandidate> afterElements,
        SystemContextSnapshot afterSystem)
    {
        var before = SemanticLiveStateMap(beforeElements, beforeSystem.ForegroundProcess);
        var after = SemanticLiveStateMap(afterElements, afterSystem.ForegroundProcess);

        foreach (var pair in before)
        {
            if (after.TryGetValue(pair.Key, out var afterState) &&
                !string.Equals(pair.Value, afterState, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static Dictionary<string, string> SemanticLiveStateMap(
        IReadOnlyList<UiElementCandidate> elements,
        string foregroundProcess)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in elements.Where(x => x.Interactable && IsRelevantProcess(x.ProcessName, foregroundProcess)))
        {
            var identity = StableLiveElementIdentity(item);
            if (map.ContainsKey(identity)) continue;
            map[identity] = SemanticLiveState(item);
        }
        return map;
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

    private static string StableLiveElementKey(UiElementCandidate x) =>
        $"{StableLiveElementIdentity(x)}|{SemanticLiveState(x)}";

    private static string StableLiveElementIdentity(UiElementCandidate x)
    {
        var bx = (int)Math.Round(x.X / 24d);
        var by = (int)Math.Round(x.Y / 24d);
        var bw = (int)Math.Round(x.Width / 24d);
        var bh = (int)Math.Round(x.Height / 24d);
        var stableName = IsStableNamedControl(x.ControlType) ? NormalizeStableName(x.Name) : string.Empty;
        return $"{x.ProcessName}|{x.ControlType}|{x.AutomationId}|{x.ClassName}|{stableName}|{bx},{by},{bw},{bh}";
    }

    private static string SemanticLiveState(UiElementCandidate x) => x.Interactable
        ? $"focus={x.Focused};toggle={x.ToggleState ?? string.Empty};selected={x.Selected?.ToString() ?? string.Empty};expand={x.ExpandCollapseState ?? string.Empty}"
        : string.Empty;

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

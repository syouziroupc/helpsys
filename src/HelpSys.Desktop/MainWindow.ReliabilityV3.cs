using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using HelpSys.Models;
using HelpSys.Services;

namespace HelpSys;

public partial class MainWindow
{
    private enum ActionVerificationResult
    {
        Success,
        NoEffect,
        Inconclusive
    }

    private GuideDecision? _v3TrackedDecision;
    private bool _v3TypeActivityObserved;

    private void MainWindow_ReliabilityV3Loaded(object sender, RoutedEventArgs e)
    {
        MainWindow_StableLoaded(sender, e);
    }

    private void MainWindow_ReliabilityV3Closing(object? sender, CancelEventArgs e)
    {
        MainWindow_StableClosing(sender, e);
    }

    private void SpeakButton_Click_V3(object sender, RoutedEventArgs e)
    {
        _speechOutput.Speak(_lastInstruction ?? StateText.Text, allowRepeat: true);
    }

    private async void OnObservedLeftClickV3(Point point)
    {
        try
        {
            await HandleObservedLeftClickV3Async(point);
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch
        {
            try
            {
                if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
                {
                    var generation = _sessionState.Generation;
                    HandleTechnicalPlanningUncertainty("マウス操作の結果監視で現在状態を確定できない", generation);
                }
            }
            catch { }
        }
    }

    private async Task HandleObservedLeftClickV3Async(Point point)
    {
        if (_sessionState.State != GuidanceSessionState.AwaitingUserAction || _currentDecision is null || _guidedBounds is null) return;
        var action = _currentDecision.Action;
        if (!action.Equals("left_click", StringComparison.OrdinalIgnoreCase) &&
            !action.Equals("double_click", StringComparison.OrdinalIgnoreCase)) return;

        var bounds = _guidedBounds.Value;
        bounds.Inflate(7, 7);
        if (!bounds.Contains(point)) return;

        if (action.Equals("double_click", StringComparison.OrdinalIgnoreCase))
        {
            var now = DateTime.UtcNow;
            var configuredWindowMs = Math.Clamp((double)GetDoubleClickTime(), 200d, 5000d);
            if (_doubleClickCount == 0 || (now - _lastGuidedClickUtc).TotalMilliseconds > configuredWindowMs)
                _doubleClickCount = 1;
            else
                _doubleClickCount++;
            _lastGuidedClickUtc = now;
            if (_doubleClickCount < 2) return;
        }

        _speechOutput.Stop();
        await CompleteCurrentStepV3Async();
    }

    private async void OnObservedKeyReleasedV3(KeyObservation observation)
    {
        try
        {
            await HandleObservedKeyReleasedV3Async(observation);
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch
        {
            try
            {
                if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
                {
                    var generation = _sessionState.Generation;
                    HandleTechnicalPlanningUncertainty("キー操作の結果監視で現在状態を確定できない", generation);
                }
            }
            catch { }
        }
    }

    private async Task HandleObservedKeyReleasedV3Async(KeyObservation observation)
    {
        if (_sessionState.State != GuidanceSessionState.AwaitingUserAction || _currentDecision is null) return;

        if (!ReferenceEquals(_v3TrackedDecision, _currentDecision))
        {
            _v3TrackedDecision = _currentDecision;
            _v3TypeActivityObserved = false;
        }

        if (_currentDecision.Action.Equals("type_text", StringComparison.OrdinalIgnoreCase))
        {
            var expected = string.IsNullOrWhiteSpace(_currentDecision.Key) ? "Enter" : _currentDecision.Key;
            if (MatchesKeySpecV3(expected, observation))
            {
                if (Volatile.Read(ref _typeTextFocusRecoveryInFlight) != 0) return;

                if (!_v3TypeActivityObserved)
                {
                    SetState("先に青い枠の入力欄へ案内された文字を入力してから、「Enter」と書かれたキーを1回押してください。", speak: true);
                    return;
                }

                _speechOutput.Stop();
                await CompleteCurrentStepV3Async();
                return;
            }

            if (LooksLikeTextEntry(observation)) _v3TypeActivityObserved = true;
            return;
        }

        if (_currentDecision.Action.Equals("press_key", StringComparison.OrdinalIgnoreCase) &&
            MatchesKeySpecV3(_currentDecision.Key, observation))
        {
            _speechOutput.Stop();
            await CompleteCurrentStepV3Async();
        }
    }

    private async Task CompleteCurrentStepV3Async()
    {
        if (_currentDecision is null || _activeRequest is null || _sessionCts is null) return;
        var generation = _sessionState.Generation;
        if (!_sessionState.TryTransition(generation, GuidanceSessionState.Verifying)) return;

        var decision = _currentDecision;
        var targetName = _localChoiceTargetActive
            ? "利用者が選んだアカウント"
            : _currentTarget is null ? (decision.Key ?? "キーボード操作") : DisplayName(_currentTarget.Name, _currentTarget.ControlType);
        bool replan = false;
        bool advance = false;
        bool inconclusive = false;

        try
        {
            SetState("操作の結果を確認しています…", speak: false);
            var verification = await WaitForStableStateTransitionV3Async(
                decision.Action,
                _stepBaseline,
                _stepSystemBaseline,
                _sessionCts.Token);
            if (!_sessionState.IsCurrent(generation)) return;

            if (verification == ActionVerificationResult.NoEffect)
            {
                _consecutiveFailures++;
                _doubleClickCount = 0;
                RecordOutlawGuidanceOutcome(
                    decision,
                    _currentTarget,
                    _stepBaseline,
                    _stepSystemBaseline,
                    _guidedBounds,
                    changed: false);
                _history.Add(new GuideHistoryItem(
                    _stepNumber,
                    $"no_effect_{decision.Action}",
                    targetName,
                    "安定した観測で期待結果が無いことを確認したため、同じ操作を繰り返さず現在状態から再計画する。"));
                var historyLimit = OutlawModePolicy.Enabled ? 64 : 12;
                if (_history.Count > historyLimit) _history.RemoveAt(0);
                ClearCurrentGuidanceV3();
                if (OutlawModePolicy.Enabled && _lastOutlawObservation is not null && _lastOutlawFrame is not null)
                {
                    _reuseLastOutlawObservationOnce = true;
                    LocalLogService.Write(
                        "outlaw_same_observation_retry_armed",
                        $"sequence={_lastOutlawObservation.Sequence};fingerprint={_lastOutlawObservation.Fingerprint};action={decision.Action}");
                }
                replan = true;
            }
            else if (verification == ActionVerificationResult.Success)
            {
                _consecutiveFailures = 0;
                if (OutlawModePolicy.Enabled)
                {
                    LocalLogService.Write(
                        "outlaw_progress_verified",
                        $"action={decision.Action};target={decision.TargetId ?? "none"};verification=expected_effect");
                }
                RecordOutlawGuidanceOutcome(
                    decision,
                    _currentTarget,
                    _stepBaseline,
                    _stepSystemBaseline,
                    _guidedBounds,
                    changed: true);
                _history.Add(new GuideHistoryItem(++_stepNumber, decision.Action, targetName, decision.Instruction));
                var historyLimit = OutlawModePolicy.Enabled ? 64 : 12;
                if (_history.Count > historyLimit) _history.RemoveAt(0);
                ClearCurrentGuidanceV3();
                _rejectedVisionTargets = 0;
                advance = true;
            }
            else
            {
                LocalLogService.Write(
                    "action_verification_inconclusive",
                    $"action={decision.Action};target={decision.TargetId ?? "none"};state={_lastObservationFingerprint}");
                _history.Add(new GuideHistoryItem(
                    _stepNumber,
                    $"verification_inconclusive_{decision.Action}",
                    targetName,
                    "操作結果を成功とも無効とも確定できないため、失敗として記録せず現在状態を1回だけ再観測する。"));
                var historyLimit = OutlawModePolicy.Enabled ? 64 : 12;
                if (_history.Count > historyLimit) _history.RemoveAt(0);
                ClearCurrentGuidanceV3();
                inconclusive = true;
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!_sessionState.IsCurrent(generation) || _sessionCts is null || _sessionCts.IsCancellationRequested || _activeRequest is null) return;

        if (inconclusive)
        {
            HandleTechnicalPlanningUncertainty("操作結果の検証が不確定", generation);
            return;
        }

        if (replan)
        {
            SetState("同じ操作は繰り返さず、現在の画面から案内を作り直しています…", speak: false);
            await AdvanceGuideAsync();
            return;
        }

        if (advance)
        {
            SetState("画面が変わったことを確認しました。次を確認しています…", speak: false);
            await Task.Delay(250, _sessionCts.Token);
            if (_sessionState.IsCurrent(generation)) await AdvanceGuideAsync();
        }
    }

    private async Task<bool> RevalidateCurrentTargetV3Async(CancellationToken cancellationToken)
    {
        if (_currentTarget is not null)
        {
            var rootProcessId = _stepSystemBaseline?.ForegroundProcessId ?? 0;
            var fresh = rootProcessId > 0
                ? await _scanner.RevalidateCandidateAsync(_currentTarget, rootProcessId, cancellationToken)
                : await _scanner.RevalidateCandidateAsync(_currentTarget, cancellationToken);
            if (fresh is null) return false;
            _currentTarget = fresh;
            _guidedBounds = fresh.Bounds;
            _overlay.ShowTarget(fresh.Bounds, _currentDecision?.Instruction ?? string.Empty);
            return true;
        }

        if (_currentDecision is not null &&
            string.Equals(_currentDecision.TargetId, "vision-target", StringComparison.Ordinal) &&
            _guidedBounds is { } visionBounds)
        {
            _overlay.Hide();
            await Task.Delay(35, cancellationToken);
            var freshBounds = await _scanner.SnapToAccessibleBoundsAsync(visionBounds, cancellationToken);
            if (freshBounds is null || freshBounds.Value.IsEmpty) return false;
            _guidedBounds = freshBounds.Value;
            _overlay.ShowTarget(freshBounds.Value, _currentDecision.Instruction);
            return true;
        }

        return _currentDecision?.Action.Equals("press_key", StringComparison.OrdinalIgnoreCase) == true;
    }

    private void ClearCurrentGuidanceV3()
    {
        _speechOutput.Stop();
        _overlay.Hide();
        _keyHint.Hide();
        _currentDecision = null;
        _currentTarget = null;
        _stepBaseline = [];
        _stepSystemBaseline = null;
        _guidedBounds = null;
        _doubleClickCount = 0;
        _validatedVisionInstruction = null;
        _v3TrackedDecision = null;
        _v3TypeActivityObserved = false;
        _localChoiceTargetActive = false;
    }

    private async Task<ActionVerificationResult> WaitForStableStateTransitionV3Async(
        string action,
        IReadOnlyList<UiElementCandidate> before,
        SystemContextSnapshot? systemBefore,
        CancellationToken cancellationToken)
    {
        var strong = action.Equals("double_click", StringComparison.OrdinalIgnoreCase) ||
                     action.Equals("type_text", StringComparison.OrdinalIgnoreCase);
        var allowFocusOnly = action.Equals("press_key", StringComparison.OrdinalIgnoreCase) ||
                             (action.Equals("left_click", StringComparison.OrdinalIgnoreCase) &&
                              _currentTarget?.ControlType is "Edit" or "ComboBox");
        var targetBefore = _currentTarget;
        var expectation = _currentDecision is not null
            ? ActionExpectation.From(_currentDecision, targetBefore)
            : new ActionExpectation(ActionEffectKind.NavigationOrContentChange, action, targetBefore);

        // The watcher is WinEvent-based. Do not poll UIA repeatedly while waiting for the action.
        var changedSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler handler = (_, _) => changedSignal.TrySetResult(true);
        _liveWatcher.Changed += handler;
        try
        {
            var immediateSystem = _systemContext.Capture();
            if (HasExpectedSystemTransitionV3(expectation, systemBefore, immediateSystem, _activeRequest))
                return ActionVerificationResult.Success;

            var initialDelay = action.Equals("double_click", StringComparison.OrdinalIgnoreCase)
                ? TimeSpan.FromMilliseconds(420)
                : TimeSpan.FromMilliseconds(220);
            await Task.Delay(initialDelay, cancellationToken);

            var timeout = Task.Delay(TimeSpan.FromMilliseconds(2600), cancellationToken);
            await Task.WhenAny(changedSignal.Task, timeout);
            cancellationToken.ThrowIfCancellationRequested();

            if (changedSignal.Task.IsCompleted)
                await Task.Delay(180, cancellationToken);

            ObservationSnapshot afterSnapshot;
            try
            {
                afterSnapshot = await _observationBroker.CaptureAsync(240, cancellationToken);
            }
            catch (ObservationChangedException)
            {
                var current = _systemContext.Capture();
                return HasExpectedSystemTransitionV3(expectation, systemBefore, current, _activeRequest)
                    ? ActionVerificationResult.Success
                    : ActionVerificationResult.Inconclusive;
            }
            catch (InvalidOperationException)
            {
                return ActionVerificationResult.Inconclusive;
            }

            var expectedEffect = HasExpectedEffectV3(
                expectation,
                before,
                systemBefore,
                afterSnapshot.Elements,
                afterSnapshot.System,
                strong,
                allowFocusOnly,
                _activeRequest);

            if (expectedEffect) return ActionVerificationResult.Success;

            // A real Windows event occurred, but the bounded verifier could not map it to the
            // expected effect. Treat that as unknown rather than poisoning the failed-action memory.
            return changedSignal.Task.IsCompleted
                ? ActionVerificationResult.Inconclusive
                : ActionVerificationResult.NoEffect;
        }
        finally
        {
            _liveWatcher.Changed -= handler;
        }
    }

    private static bool HasExpectedEffectV3(
        ActionExpectation expectation,
        IReadOnlyList<UiElementCandidate> before,
        SystemContextSnapshot? systemBefore,
        IReadOnlyList<UiElementCandidate> after,
        SystemContextSnapshot systemAfter,
        bool strong,
        bool allowFocusOnly,
        string? goal)
    {
        if (HasExpectedSystemTransitionV3(expectation, systemBefore, systemAfter, goal)) return true;

        var targetBefore = expectation.Target;
        var current = targetBefore is null ? null : FindMatchingTargetV3(targetBefore, after);

        switch (expectation.Kind)
        {
            case ActionEffectKind.FocusTarget:
                return targetBefore is not null &&
                       current is not null &&
                       !targetBefore.Focused &&
                       current.Focused;

            case ActionEffectKind.ToggleOrSelection:
                if (targetBefore is null || current is null) return false;
                var toggled = !string.Equals(
                    targetBefore.ToggleState,
                    current.ToggleState,
                    StringComparison.Ordinal) &&
                    (targetBefore.ToggleState is not null || current.ToggleState is not null);
                var selected = targetBefore.Selected != current.Selected &&
                               (targetBefore.Selected.HasValue || current.Selected.HasValue);
                return toggled || selected;

            case ActionEffectKind.SelectionOrExpansion:
                if (targetBefore is null || current is null) return false;
                var selectionChanged = targetBefore.Selected != current.Selected &&
                                       (targetBefore.Selected.HasValue || current.Selected.HasValue);
                var expansionChanged = !string.Equals(
                    targetBefore.ExpandCollapseState,
                    current.ExpandCollapseState,
                    StringComparison.Ordinal) &&
                    (targetBefore.ExpandCollapseState is not null || current.ExpandCollapseState is not null);
                return selectionChanged || expansionChanged;

            case ActionEffectKind.TextSubmission:
            case ActionEffectKind.NavigationOrContentChange:
            default:
                return HasStableTransitionV3(
                    before,
                    systemBefore,
                    after,
                    systemAfter,
                    strong,
                    allowFocusOnly,
                    expectation,
                    goal);
        }
    }

    private static bool HasStableTransitionV3(
        IReadOnlyList<UiElementCandidate> before,
        SystemContextSnapshot? systemBefore,
        IReadOnlyList<UiElementCandidate> after,
        SystemContextSnapshot systemAfter,
        bool strong,
        bool allowFocusOnly,
        ActionExpectation expectation,
        string? goal)
    {
        var targetBefore = expectation.Target;
        var action = expectation.Action;
        if (HasExpectedSystemTransitionV3(expectation, systemBefore, systemAfter, goal)) return true;
        if (HasActionSpecificTransitionV3(targetBefore, after, action, systemBefore)) return true;

        var foreground = systemBefore?.ForegroundProcess ?? systemAfter.ForegroundProcess;
        var semanticEvidence = HasExpectedSemanticEvidenceV3(expectation, goal, after, systemAfter);
        var targetDisappeared = targetBefore is not null && FindMatchingTargetV3(targetBefore, after) is null;
        if (HasWindowSetTransitionV3(before, after, foreground) && (semanticEvidence || targetDisappeared)) return true;

        if (allowFocusOnly)
        {
            var beforeFocus = before.FirstOrDefault(x => x.Focused && IsRelevantProcess(x.ProcessName, foreground));
            var afterFocus = after.FirstOrDefault(x => x.Focused && IsRelevantProcess(x.ProcessName, foreground));
            var beforeKey = beforeFocus is null ? string.Empty : FocusKeyV3(beforeFocus);
            var afterKey = afterFocus is null ? string.Empty : FocusKeyV3(afterFocus);
            if (!string.Equals(beforeKey, afterKey, StringComparison.Ordinal) &&
                (!string.IsNullOrEmpty(beforeKey) || !string.IsNullOrEmpty(afterKey))) return true;
        }

        var beforeKeys = StableContentKeysV3(before, foreground);
        var afterKeys = StableContentKeysV3(after, foreground);
        if (beforeKeys.Count == 0 || afterKeys.Count == 0)
            return beforeKeys.Count != afterKeys.Count && (semanticEvidence || targetDisappeared);

        var countThreshold = strong ? 12 : 10;
        var countChanged = Math.Abs(beforeKeys.Count - afterKeys.Count) >= countThreshold;
        var overlap = beforeKeys.Count(x => afterKeys.Contains(x));
        var similarity = overlap / (double)Math.Max(beforeKeys.Count, afterKeys.Count);
        var substantialContentChange = countChanged || similarity < (strong ? 0.72 : 0.82);
        return substantialContentChange && (semanticEvidence || targetDisappeared);
    }

    private static bool HasExpectedSystemTransitionV3(
        ActionExpectation expectation,
        SystemContextSnapshot? before,
        SystemContextSnapshot after,
        string? goal)
    {
        if (before is null) return false;
        if (after.ForegroundProcessId <= 0 || string.IsNullOrWhiteSpace(after.ForegroundProcess)) return false;

        var ownershipChanged =
            (before.ForegroundProcessId > 0 && after.ForegroundProcessId > 0 &&
             before.ForegroundProcessId != after.ForegroundProcessId) ||
            !before.ForegroundProcess.Equals(after.ForegroundProcess, StringComparison.OrdinalIgnoreCase);

        var browserChanged = false;
        if (before.Browser is not null || after.Browser is not null)
        {
            var beforeTitle = before.ForegroundTitle?.Trim() ?? string.Empty;
            var afterTitle = after.ForegroundTitle?.Trim() ?? string.Empty;
            var beforeUrl = before.Browser?.Url ?? string.Empty;
            var afterUrl = after.Browser?.Url ?? string.Empty;
            browserChanged =
                (!string.IsNullOrWhiteSpace(afterTitle) &&
                 !beforeTitle.Equals(afterTitle, StringComparison.Ordinal)) ||
                (!string.IsNullOrWhiteSpace(afterUrl) &&
                 !beforeUrl.Equals(afterUrl, StringComparison.OrdinalIgnoreCase));
        }

        if (!ownershipChanged && !browserChanged) return false;
        return HasExpectedSemanticEvidenceV3(expectation, goal, Array.Empty<UiElementCandidate>(), after);
    }

    private static bool HasExpectedSemanticEvidenceV3(
        ActionExpectation expectation,
        string? goal,
        IReadOnlyList<UiElementCandidate> after,
        SystemContextSnapshot systemAfter)
    {
        var anchors = BuildExpectedStateAnchorsV3(goal, expectation.Target?.Name);
        if (anchors.Count == 0) return false;

        var visibleText = string.Join(
            " ",
            after
                .Where(x => !x.Password && x.ControlType is not ("Edit" or "ComboBox"))
                .Select(x => x.Name)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Take(120));

        var haystack = $"{systemAfter.ForegroundProcess} {systemAfter.ForegroundTitle} {systemAfter.Browser?.Domain} {systemAfter.Browser?.Url} {visibleText}"
            .ToLowerInvariant();

        return anchors.Any(anchor => haystack.Contains(anchor, StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<string> BuildExpectedStateAnchorsV3(string? goal, string? targetName)
    {
        var source = $"{goal ?? string.Empty} {targetName ?? string.Empty}";
        var anchors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stop = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "click","button","left","right","open","search","enter","next",
            "クリック","ボタン","押す","押して","開く","検索","次へ","画面","案内"
        };

        foreach (Match match in Regex.Matches(source, @"[A-Za-z][A-Za-z0-9._:/-]{2,}"))
        {
            var token = match.Value.Trim().Trim('.', ',', ':', ';', '/', '\\').ToLowerInvariant();
            if (token.Length >= 3 && token.Length <= 80 && !stop.Contains(token))
                anchors.Add(token);
        }

        if (!string.IsNullOrWhiteSpace(targetName))
        {
            foreach (var piece in Regex.Split(targetName, @"[\s\p{P}\p{S}]+"))
            {
                var token = piece.Trim().ToLowerInvariant();
                if (token.Length >= 2 && token.Length <= 24 && !stop.Contains(token))
                    anchors.Add(token);
            }
        }

        return anchors.Take(12).ToArray();
    }

    private static bool HasActionSpecificTransitionV3(
        UiElementCandidate? beforeTarget,
        IReadOnlyList<UiElementCandidate> after,
        string action,
        SystemContextSnapshot? systemBefore)
    {
        if (beforeTarget is null) return false;
        var current = FindMatchingTargetV3(beforeTarget, after);
        if (current is null) return false;

        var type = beforeTarget.ControlType;
        if (type is "CheckBox" or "RadioButton")
        {
            if (!string.Equals(beforeTarget.ToggleState, current.ToggleState, StringComparison.Ordinal) &&
                (beforeTarget.ToggleState is not null || current.ToggleState is not null)) return true;
            if (beforeTarget.Selected != current.Selected && (beforeTarget.Selected.HasValue || current.Selected.HasValue)) return true;
        }

        if (type is "ListItem" or "TabItem" or "TreeItem")
        {
            if (beforeTarget.Selected != current.Selected && (beforeTarget.Selected.HasValue || current.Selected.HasValue)) return true;
            if (!string.Equals(beforeTarget.ExpandCollapseState, current.ExpandCollapseState, StringComparison.Ordinal) &&
                (beforeTarget.ExpandCollapseState is not null || current.ExpandCollapseState is not null)) return true;
        }

        if (action.Equals("left_click", StringComparison.OrdinalIgnoreCase) && type is "Edit" or "ComboBox")
        {
            if (!beforeTarget.Focused && current.Focused) return true;
        }

        // A changed Edit value proves only that typing happened. type_text is completed only
        // after the finishing key causes a stable system/window/content transition.

        return false;
    }

    private static UiElementCandidate? FindMatchingTargetV3(UiElementCandidate target, IReadOnlyList<UiElementCandidate> candidates)
    {
        var sameProcess = candidates.Where(x => x.ProcessId == target.ProcessId &&
                                                x.ControlType.Equals(target.ControlType, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(target.AutomationId))
        {
            var byId = sameProcess.Where(x => x.AutomationId.Equals(target.AutomationId, StringComparison.Ordinal)).ToArray();
            if (byId.Length == 1) return byId[0];
            if (byId.Length > 1)
                return byId.OrderBy(x => CenterDistanceV3(target, x)).FirstOrDefault();
        }

        if (!string.IsNullOrWhiteSpace(target.Name))
        {
            var byName = sameProcess.Where(x => x.Name.Equals(target.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (byName.Length > 0) return byName.OrderBy(x => CenterDistanceV3(target, x)).FirstOrDefault();
        }

        return sameProcess
            .Where(x => x.ClassName.Equals(target.ClassName, StringComparison.Ordinal))
            .OrderBy(x => CenterDistanceV3(target, x))
            .FirstOrDefault(x => CenterDistanceV3(target, x) <= 80);
    }

    private static double CenterDistanceV3(UiElementCandidate a, UiElementCandidate b)
    {
        var ax = a.X + a.Width / 2d;
        var ay = a.Y + a.Height / 2d;
        var bx = b.X + b.Width / 2d;
        var by = b.Y + b.Height / 2d;
        return Math.Sqrt(Math.Pow(ax - bx, 2) + Math.Pow(ay - by, 2));
    }

    private static bool HasSystemTransitionV3(SystemContextSnapshot? before, SystemContextSnapshot after)
    {
        if (before is null) return false;
        // A transient failure to resolve the foreground window is not evidence that an action succeeded.
        if (after.ForegroundProcessId <= 0 || string.IsNullOrWhiteSpace(after.ForegroundProcess)) return false;
        if (before.ForegroundProcessId > 0 && after.ForegroundProcessId > 0 && before.ForegroundProcessId != after.ForegroundProcessId) return true;
        if (!before.ForegroundProcess.Equals(after.ForegroundProcess, StringComparison.OrdinalIgnoreCase)) return true;

        // Browser navigations can keep the same process and very similar UIA geometry. Window title
        // is a local-only signal and catches result-page transitions even when URL capture is stale
        // or unavailable. Stability is still required by WaitForStableStateTransitionV3Async.
        if (before.Browser is not null && after.Browser is not null)
        {
            var beforeTitle = before.ForegroundTitle?.Trim() ?? string.Empty;
            var afterTitle = after.ForegroundTitle?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(afterTitle) &&
                !beforeTitle.Equals(afterTitle, StringComparison.Ordinal)) return true;
        }

        var beforeUrl = before.Browser?.Url ?? string.Empty;
        var afterUrl = after.Browser?.Url ?? string.Empty;
        return !beforeUrl.Equals(afterUrl, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(afterUrl);
    }

    private static bool HasWindowSetTransitionV3(IReadOnlyList<UiElementCandidate> before, IReadOnlyList<UiElementCandidate> after, string foreground)
    {
        var beforeWindows = before
            .Where(x => x.ControlType.Equals("Window", StringComparison.OrdinalIgnoreCase) && IsRelevantProcess(x.ProcessName, foreground))
            .Select(WindowKeyV3)
            .ToHashSet(StringComparer.Ordinal);
        var afterWindows = after
            .Where(x => x.ControlType.Equals("Window", StringComparison.OrdinalIgnoreCase) && IsRelevantProcess(x.ProcessName, foreground))
            .Select(WindowKeyV3)
            .ToHashSet(StringComparer.Ordinal);
        return !beforeWindows.SetEquals(afterWindows);
    }

    private static HashSet<string> StableContentKeysV3(IReadOnlyList<UiElementCandidate> elements, string foreground)
    {
        return elements
            .Where(x => IsRelevantProcess(x.ProcessName, foreground))
            .Where(x => x.Interactable || x.ControlType is "Window" or "Pane" or "Document" or "Text")
            .Select(ContentKeyV3)
            .Take(220)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string WindowKeyV3(UiElementCandidate x)
    {
        var bx = (int)Math.Round(x.X / 32d);
        var by = (int)Math.Round(x.Y / 32d);
        var bw = (int)Math.Round(x.Width / 32d);
        var bh = (int)Math.Round(x.Height / 32d);
        return $"{x.ProcessId}|{x.ProcessName}|{x.AutomationId}|{x.ClassName}|{bx},{by},{bw},{bh}";
    }

    private static string FocusKeyV3(UiElementCandidate x)
    {
        var bx = (int)Math.Round(x.X / 24d);
        var by = (int)Math.Round(x.Y / 24d);
        return $"{x.ProcessId}|{x.ControlType}|{x.AutomationId}|{x.ClassName}|{bx},{by}";
    }

    private static string ContentKeyV3(UiElementCandidate x)
    {
        var bx = (int)Math.Round(x.X / 24d);
        var by = (int)Math.Round(x.Y / 24d);
        var bw = (int)Math.Round(x.Width / 24d);
        var bh = (int)Math.Round(x.Height / 24d);

        // Names from visible static text are useful for local transition detection, especially on
        // search-result pages whose layout remains almost unchanged. Never place the raw text in the
        // key: hash it locally and discard it. Input controls remain excluded from this content token.
        var localTextToken = x.Password || x.ControlType is "Edit" or "ComboBox"
            ? string.Empty
            : LocalTextFingerprintV3(x.Name);
        return $"{x.ProcessId}|{x.ControlType}|{x.AutomationId}|{x.ClassName}|{localTextToken}|{bx},{by},{bw},{bh}";
    }

    private static string LocalTextFingerprintV3(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var normalized = NormalizeStableName(value);
        if (string.IsNullOrWhiteSpace(normalized)) return string.Empty;
        if (normalized.Length > 180) normalized = normalized[..180];

        // FNV-1a is sufficient here: the token is process-local, ephemeral and never leaves HelpSys.
        ulong hash = 1469598103934665603UL;
        foreach (var ch in normalized)
        {
            hash ^= char.ToLowerInvariant(ch);
            hash *= 1099511628211UL;
        }
        return hash.ToString("x16");
    }

    private static bool MatchesKeySpecV3(string? keySpec, KeyObservation observation)
    {
        if (string.IsNullOrWhiteSpace(keySpec)) return false;
        var parts = keySpec.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.ToLowerInvariant()).ToArray();
        if (parts.Length == 0) return false;

        var needCtrl = parts.Any(x => x is "ctrl" or "control");
        var needAlt = parts.Contains("alt");
        var needShift = parts.Contains("shift");
        var needWindows = parts.Any(x => x is "windows" or "win");

        if (observation.Control != needCtrl || observation.Alt != needAlt ||
            observation.Shift != needShift || observation.Windows != needWindows) return false;

        var main = parts.LastOrDefault(x => x is not ("ctrl" or "control" or "alt" or "shift" or "windows" or "win"));
        if (main is null) return needWindows && observation.VirtualKey is 0x5B or 0x5C;
        return VirtualKeyV3(main) == observation.VirtualKey;
    }

    private static bool LooksLikeTextEntry(KeyObservation observation)
    {
        if (observation.Windows || observation.Alt) return false;
        if (observation.Control) return observation.VirtualKey == 0x56;
        var key = observation.VirtualKey;
        return key is >= 0x30 and <= 0x5A or >= 0x60 and <= 0x69 or 0x08 or 0x20 or 0x2E ||
               key is >= 0xBA and <= 0xE2;
    }

    private static int VirtualKeyV3(string key) => key switch
    {
        "enter" or "return" => 0x0D,
        "tab" => 0x09,
        "escape" or "esc" => 0x1B,
        "space" => 0x20,
        "delete" or "del" => 0x2E,
        "backspace" => 0x08,
        "insert" or "ins" => 0x2D,
        "home" => 0x24,
        "end" => 0x23,
        "pageup" or "pgup" => 0x21,
        "pagedown" or "pgdn" => 0x22,
        "left" => 0x25,
        "up" => 0x26,
        "right" => 0x27,
        "down" => 0x28,
        _ when key.Length >= 2 && key[0] == 'f' && int.TryParse(key[1..], out var f) && f is >= 1 and <= 24 => 0x6F + f,
        _ when key.Length == 1 && key[0] is >= 'a' and <= 'z' => char.ToUpperInvariant(key[0]),
        _ when key.Length == 1 && char.IsDigit(key[0]) => key[0],
        _ => -1
    };

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();
}

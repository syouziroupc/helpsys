using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using HelpSys.Models;
using HelpSys.Services;

namespace HelpSys;

public partial class MainWindow
{
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
                    await RecoverFromObserverFailureAsync("マウス操作の結果監視で現在状態を確定できない");
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
                    await RecoverFromObserverFailureAsync("キー操作の結果監視で現在状態を確定できない");
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
        var targetName = _currentTarget is null ? (decision.Key ?? "キーボード操作") : DisplayName(_currentTarget.Name, _currentTarget.ControlType);
        string? retryMessage = null;
        string? routeRecoveryIssue = null;
        bool replan = false;
        bool advance = false;
        bool forceVision = false;

        try
        {
            SetState("操作の結果を確認しています…", speak: false);
            var changed = await WaitForStableStateTransitionV3Async(decision.Action, _stepBaseline, _stepSystemBaseline, _sessionCts.Token);
            if (!_sessionState.IsCurrent(generation)) return;

            if (!changed)
            {
                _consecutiveFailures++;
                _doubleClickCount = 0;

                if (_consecutiveFailures == 1)
                {
                    var stillValid = await RevalidateCurrentTargetV3Async(_sessionCts.Token);
                    if (!_sessionState.IsCurrent(generation)) return;
                    if (!stillValid)
                    {
                        _history.Add(new GuideHistoryItem(_stepNumber, $"stale_{decision.Action}", targetName, "再試行前に対象が消えたため、同じ操作を繰り返さず現在画面から再計画する。"));
                        if (_history.Count > 12) _history.RemoveAt(0);
                        ClearCurrentGuidanceV3();
                        replan = true;
                    }
                    else
                    {
                        _stepSystemBaseline = _systemContext.Capture();
                        if (!HasUsableForeground(_stepSystemBaseline))
                        {
                            _history.Add(new GuideHistoryItem(_stepNumber, "foreground_lost", targetName, "操作後の前面アプリを一時的に特定できないため、停止せず現在位置を再取得して復帰経路を探す。"));
                            if (_history.Count > 12) _history.RemoveAt(0);
                            ClearCurrentGuidanceV3();
                            routeRecoveryIssue = "操作後の前面アプリを一時的に特定できない";
                        }
                        else
                        {
                            _stepBaseline = await _scanner.CaptureCandidatesForProcessAsync(_stepSystemBaseline.ForegroundProcessId, 420, _sessionCts.Token);
                            if (!_sessionState.IsCurrent(generation)) return;

                            if (decision.Action.Equals("type_text", StringComparison.OrdinalIgnoreCase))
                                retryMessage = "まだ次の画面へ進んでいません。入力欄の文字が正しければ、文字は追加せず「Enter」と書かれたキーを1回押してください。";
                            else if (decision.Action.Equals("double_click", StringComparison.OrdinalIgnoreCase))
                                retryMessage = "まだ画面が変わっていません。同じ青い枠の場所で、マウスの左ボタンを間をあけずに2回押してください。";
                            else
                                retryMessage = $"まだ画面が変わっていません。青い枠が同じ場所にあることを確認して、もう一度同じ操作をしてください。{decision.Instruction}";
                        }
                    }
                }
                else
                {
                    _history.Add(new GuideHistoryItem(_stepNumber, $"failed_{decision.Action}", targetName, decision.Instruction));
                    if (_history.Count > 12) _history.RemoveAt(0);
                    ClearCurrentGuidanceV3();
                    if (_consecutiveFailures == 2)
                    {
                        _forceVisionNext = true;
                        forceVision = true;
                    }
                    else
                    {
                        routeRecoveryIssue = "同じ操作を複数回行っても状態が変わらないため、別の安全な経路を選ぶ";
                    }
                }
            }
            else
            {
                _consecutiveFailures = 0;
                _history.Add(new GuideHistoryItem(++_stepNumber, decision.Action, targetName, decision.Instruction));
                if (_history.Count > 12) _history.RemoveAt(0);
                ClearCurrentGuidanceV3();
                _rejectedVisionTargets = 0;
                advance = true;
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!_sessionState.IsCurrent(generation) || _sessionCts is null || _sessionCts.IsCancellationRequested || _activeRequest is null) return;

        if (retryMessage is not null)
        {
            if (!_sessionState.TryTransition(generation, GuidanceSessionState.AwaitingUserAction)) return;
            SetState(retryMessage, speak: true);
            return;
        }

        if (routeRecoveryIssue is not null)
        {
            await TryRouteRecoveryAsync(routeRecoveryIssue, generation, _sessionCts.Token);
            return;
        }

        if (replan)
        {
            SetState("案内していた場所が変わったため、現在の画面から案内を作り直しています…", speak: false);
            await AdvanceGuideAsync();
            return;
        }

        if (forceVision)
        {
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
    }

    private async Task<bool> WaitForStableStateTransitionV3Async(
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
        var rootProcessId = systemBefore?.ForegroundProcessId ?? 0;

        await Task.Delay(action.Equals("double_click", StringComparison.OrdinalIgnoreCase) ? 900 : 450, cancellationToken);
        var stopwatch = Stopwatch.StartNew();

        while (stopwatch.Elapsed < TimeSpan.FromSeconds(7.5))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var first = rootProcessId > 0
                ? await _scanner.CaptureCandidatesForProcessAsync(rootProcessId, 420, cancellationToken)
                : [];
            var firstSystem = _systemContext.Capture();
            if (!HasStableTransitionV3(before, systemBefore, first, firstSystem, strong, allowFocusOnly, targetBefore, action))
            {
                await Task.Delay(350, cancellationToken);
                continue;
            }

            await Task.Delay(strong ? 650 : 400, cancellationToken);
            var second = rootProcessId > 0
                ? await _scanner.CaptureCandidatesForProcessAsync(rootProcessId, 420, cancellationToken)
                : [];
            var secondSystem = _systemContext.Capture();
            if (HasStableTransitionV3(before, systemBefore, second, secondSystem, strong, allowFocusOnly, targetBefore, action)) return true;
        }

        return false;
    }

    private static bool HasStableTransitionV3(
        IReadOnlyList<UiElementCandidate> before,
        SystemContextSnapshot? systemBefore,
        IReadOnlyList<UiElementCandidate> after,
        SystemContextSnapshot systemAfter,
        bool strong,
        bool allowFocusOnly,
        UiElementCandidate? targetBefore,
        string action)
    {
        if (HasSystemTransitionV3(systemBefore, systemAfter)) return true;
        if (HasActionSpecificTransitionV3(targetBefore, after, action, systemBefore)) return true;

        var foreground = systemBefore?.ForegroundProcess ?? systemAfter.ForegroundProcess;
        if (HasWindowSetTransitionV3(before, after, foreground)) return true;

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
        if (beforeKeys.Count == 0 || afterKeys.Count == 0) return beforeKeys.Count != afterKeys.Count;

        var countThreshold = strong ? 12 : 10;
        if (Math.Abs(beforeKeys.Count - afterKeys.Count) >= countThreshold) return true;
        var overlap = beforeKeys.Count(x => afterKeys.Contains(x));
        var similarity = overlap / (double)Math.Max(beforeKeys.Count, afterKeys.Count);
        return similarity < (strong ? 0.72 : 0.82);
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
        var stableName = x.ControlType.ToLowerInvariant() is
            "button" or "menuitem" or "listitem" or "treeitem" or "tabitem" or "hyperlink" or "checkbox" or "radiobutton"
            ? NormalizeStableName(x.Name)
            : string.Empty;
        return $"{x.ProcessId}|{x.ControlType}|{x.AutomationId}|{x.ClassName}|{stableName}|{bx},{by},{bw},{bh}";
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

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
    private bool _v3HandlersAttached;

    private void MainWindow_ReliabilityV3Loaded(object sender, RoutedEventArgs e)
    {
        MainWindow_StableLoaded(sender, e);
        if (_v3HandlersAttached) return;

        // The original handlers remain in source for compatibility, but v3 owns live action
        // verification. Do not let both handlers advance the same step.
        _actionObserver.LeftClick -= OnObservedLeftClick;
        _actionObserver.KeyReleased -= OnObservedKeyReleased;
        _actionObserver.LeftClick += OnObservedLeftClickV3;
        _actionObserver.KeyReleased += OnObservedKeyReleasedV3;
        _v3HandlersAttached = true;
    }

    private void MainWindow_ReliabilityV3Closing(object? sender, CancelEventArgs e)
    {
        if (_v3HandlersAttached)
        {
            _actionObserver.LeftClick -= OnObservedLeftClickV3;
            _actionObserver.KeyReleased -= OnObservedKeyReleasedV3;
            _v3HandlersAttached = false;
        }
        MainWindow_StableClosing(sender, e);
    }

    private void SpeakButton_Click_V3(object sender, RoutedEventArgs e)
    {
        _speechOutput.Speak(_lastInstruction ?? StateText.Text, allowRepeat: true);
    }

    private async void OnObservedLeftClickV3(Point point)
    {
        if (_planning || _verifyingAction || _currentDecision is null || _guidedBounds is null) return;
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

            // The first press is intentionally silent. Speaking a new sentence here would arrive
            // after the OS double-click window and distract the user from making the second press.
            if (_doubleClickCount < 2) return;
        }

        _speechOutput.Stop();
        await CompleteCurrentStepV3Async();
    }

    private async void OnObservedKeyReleasedV3(KeyObservation observation)
    {
        if (_planning || _verifyingAction || _currentDecision is null) return;

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
        if (_verifyingAction || _currentDecision is null || _activeRequest is null || _sessionCts is null) return;
        _verifyingAction = true;

        var decision = _currentDecision;
        var targetName = _currentTarget is null ? (decision.Key ?? "キーボード操作") : DisplayName(_currentTarget.Name, _currentTarget.ControlType);
        string? retryMessage = null;
        string? clarification = null;
        bool replan = false;
        bool advance = false;
        bool forceVision = false;

        try
        {
            SetState("操作の結果を確認しています…", speak: false);
            var changed = await WaitForStableStateTransitionV3Async(decision.Action, _stepBaseline, _stepSystemBaseline, _sessionCts.Token);
            if (!changed)
            {
                _consecutiveFailures++;
                _doubleClickCount = 0;

                if (_consecutiveFailures == 1)
                {
                    var stillValid = await RevalidateCurrentTargetV3Async(_sessionCts.Token);
                    if (!stillValid)
                    {
                        _history.Add(new GuideHistoryItem(_stepNumber, $"stale_{decision.Action}", targetName, "再試行前に対象が消えたため、同じ操作を繰り返さず現在画面から再計画する。"));
                        if (_history.Count > 12) _history.RemoveAt(0);
                        ClearCurrentGuidanceV3();
                        replan = true;
                    }
                    else if (decision.Action.Equals("type_text", StringComparison.OrdinalIgnoreCase))
                    {
                        retryMessage = "まだ次の画面へ進んでいません。入力欄の文字が正しければ、文字は追加せず「Enter」と書かれたキーを1回押してください。";
                    }
                    else if (decision.Action.Equals("double_click", StringComparison.OrdinalIgnoreCase))
                    {
                        retryMessage = "まだ画面が変わっていません。同じ青い枠の場所で、マウスの左ボタンを間をあけずに2回押してください。";
                    }
                    else
                    {
                        retryMessage = $"まだ画面が変わっていません。青い枠が同じ場所にあることを確認して、もう一度同じ操作をしてください。{decision.Instruction}";
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
                        clarification = "同じ操作をしても画面が変わりませんでした。今、画面に何が表示されているか短く教えてください。";
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
        catch (OperationCanceledException) { }
        finally
        {
            _verifyingAction = false;
            _v3TrackedDecision = null;
            _v3TypeActivityObserved = false;
        }

        if (_sessionCts is null || _sessionCts.IsCancellationRequested || _activeRequest is null) return;

        if (retryMessage is not null)
        {
            SetState(retryMessage, speak: true);
            return;
        }

        if (clarification is not null)
        {
            WaitForClarification(clarification);
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
            await AdvanceGuideAsync();
        }
    }

    private async Task<bool> RevalidateCurrentTargetV3Async(CancellationToken cancellationToken)
    {
        if (_currentTarget is not null)
        {
            var fresh = await _scanner.RevalidateCandidateAsync(_currentTarget, cancellationToken);
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

        // Keyboard-only instructions have no screen target to revalidate.
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

        await Task.Delay(action.Equals("double_click", StringComparison.OrdinalIgnoreCase) ? 900 : 450, cancellationToken);
        var stopwatch = Stopwatch.StartNew();

        while (stopwatch.Elapsed < TimeSpan.FromSeconds(7.5))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var first = await _scanner.CaptureCandidatesAsync(420, cancellationToken);
            var firstSystem = _systemContext.Capture();
            if (!HasStableTransitionV3(before, systemBefore, first, firstSystem, strong, allowFocusOnly))
            {
                await Task.Delay(350, cancellationToken);
                continue;
            }

            // One animation frame, autocomplete popup, focus bounce or transient dialog must not
            // complete a step. Confirm that the change still exists in a second independent sample.
            await Task.Delay(strong ? 650 : 400, cancellationToken);
            var second = await _scanner.CaptureCandidatesAsync(420, cancellationToken);
            var secondSystem = _systemContext.Capture();
            if (HasStableTransitionV3(before, systemBefore, second, secondSystem, strong, allowFocusOnly)) return true;
        }

        return false;
    }

    private static bool HasStableTransitionV3(
        IReadOnlyList<UiElementCandidate> before,
        SystemContextSnapshot? systemBefore,
        IReadOnlyList<UiElementCandidate> after,
        SystemContextSnapshot systemAfter,
        bool strong,
        bool allowFocusOnly)
    {
        if (HasSystemTransitionV3(systemBefore, systemAfter)) return true;

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

    private static bool HasSystemTransitionV3(SystemContextSnapshot? before, SystemContextSnapshot after)
    {
        if (before is null) return false;
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

        // Extra modifiers change the meaning of many keys (Enter vs Ctrl+Enter, for example).
        // Match the complete chord, not merely a subset of required modifiers.
        if (observation.Control != needCtrl || observation.Alt != needAlt ||
            observation.Shift != needShift || observation.Windows != needWindows) return false;

        var main = parts.LastOrDefault(x => x is not ("ctrl" or "control" or "alt" or "shift" or "windows" or "win"));
        if (main is null) return needWindows && observation.VirtualKey is 0x5B or 0x5C;
        return VirtualKeyV3(main) == observation.VirtualKey;
    }

    private static bool LooksLikeTextEntry(KeyObservation observation)
    {
        if (observation.Windows || observation.Alt) return false;
        if (observation.Control) return observation.VirtualKey == 0x56; // Ctrl+V paste
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

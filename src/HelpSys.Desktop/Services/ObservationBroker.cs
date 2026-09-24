using HelpSys.Models;

namespace HelpSys.Services;

public sealed class ObservationBroker
{
    private static readonly HashSet<string> ShellSurfaceProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "SearchHost", "StartMenuExperienceHost", "ShellExperienceHost", "TextInputHost"
    };

    private readonly SystemContextService _systemContext;
    private readonly global::HelpSys.UiAutomationScanner _scanner;
    private long _sequence;

    public ObservationBroker(
        SystemContextService systemContext,
        global::HelpSys.UiAutomationScanner scanner)
    {
        _systemContext = systemContext;
        _scanner = scanner;
    }

    public async Task<ObservationSnapshot> CaptureAsync(
        int maxCandidates = 240,
        CancellationToken cancellationToken = default)
    {
        var before = _systemContext.Capture();
        if (!HasUsableForeground(before))
        {
            await Task.Delay(160, cancellationToken).ConfigureAwait(false);
            before = _systemContext.Capture();
        }

        if (!HasUsableForeground(before))
            throw new InvalidOperationException("前面ウィンドウを安全に特定できません。");

        var elements = await PerformanceTrace.MeasureAsync(
            "observation.scan",
            () => OutlawModePolicy.Enabled
                ? _scanner.CaptureCandidatesAsync(maxCandidates, cancellationToken)
                : _scanner.CaptureCandidatesForProcessAsync(
                    before.ForegroundProcessId,
                    maxCandidates,
                    cancellationToken)).ConfigureAwait(false);

        if (OutlawModePolicy.Enabled)
            elements = KeepForegroundProcessEvidence(elements, before);

        var after = _systemContext.Capture();
        if (!HasSameIdentity(before, after))
        {
            if (OutlawModePolicy.Enabled && HasSameProcess(before, after))
            {
                LocalLogService.Write(
                    "outlaw_observation_rebound",
                    $"reason=same_process_hwnd_changed;before={before.ForegroundProcess}/{before.ForegroundProcessId}/{before.ForegroundWindowHandle};after={after.ForegroundProcess}/{after.ForegroundProcessId}/{after.ForegroundWindowHandle}");

                // Maximize/restore and some Chromium transitions can recreate the foreground HWND
                // while staying in the same process. Re-scan once against the new stable surface
                // instead of treating that expected transition as a fatal observation change.
                elements = await PerformanceTrace.MeasureAsync(
                    "observation.rescan",
                    () => _scanner.CaptureCandidatesAsync(maxCandidates, cancellationToken)).ConfigureAwait(false);
                elements = KeepForegroundProcessEvidence(elements, after);
                var rebound = _systemContext.Capture();
                if (!HasSameIdentity(after, rebound))
                    throw new ObservationChangedException("同一アプリ内の画面切替が継続しているため、現在状態を取り直します。");
                after = rebound;
            }
            else
            {
                if (OutlawModePolicy.Enabled)
                {
                    LocalLogService.Write(
                        "outlaw_observation_discarded",
                        $"reason=foreground_changed_during_scan;before={before.ForegroundProcess}/{before.ForegroundProcessId}/{before.ForegroundWindowHandle};after={after.ForegroundProcess}/{after.ForegroundProcessId}/{after.ForegroundWindowHandle};candidates={elements.Count}");
                }

                throw new ObservationChangedException("UIA取得中に前面ウィンドウが変化しました。観測を破棄して現在状態を取り直します。");
            }
        }

        after = _systemContext.EnrichWithObservedElements(after, elements);
        var sequence = Interlocked.Increment(ref _sequence);
        return new ObservationSnapshot(
            sequence,
            DateTime.UtcNow,
            after,
            elements,
            BuildFingerprint(after, elements));
    }

    public bool IsCurrent(ObservationSnapshot snapshot)
        => HasSameIdentity(snapshot.System, _systemContext.Capture());

    private static IReadOnlyList<UiElementCandidate> KeepForegroundProcessEvidence(
        IReadOnlyList<UiElementCandidate> elements,
        SystemContextSnapshot context)
    {
        if (context.ForegroundProcessId <= 0) return elements;

        // Windows 11 shell surfaces are intentionally split across explorer,
        // StartMenuExperienceHost, SearchHost, ShellExperienceHost and TextInputHost.
        // Treat those processes as one logical UI surface. Restricting Outlaw evidence to
        // only the foreground PID removes the actual Start/Search controls while leaving
        // the desktop's explorer tree, which is exactly the failure mode seen on Start.
        if (IsShellSurfaceProcess(context.ForegroundProcess))
        {
            var shell = elements
                .Where(x => IsShellSurfaceProcess(x.ProcessName))
                .ToArray();

            if (shell.Length > 0)
            {
                LocalLogService.Write(
                    "outlaw_shell_uia_fused",
                    $"foreground={context.ForegroundProcess}/{context.ForegroundProcessId};kept={shell.Length};dropped={elements.Count - shell.Length};processes={string.Join(',', shell.Select(x => x.ProcessName).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))}");
                return shell;
            }
        }

        var coherent = elements
            .Where(x => x.ProcessId == context.ForegroundProcessId)
            .ToArray();

        if (coherent.Length == 0) return elements;

        if (coherent.Length != elements.Count)
            LocalLogService.Write(
                "outlaw_background_uia_filtered",
                $"foreground={context.ForegroundProcess}/{context.ForegroundProcessId};kept={coherent.Length};dropped={elements.Count - coherent.Length}");

        return coherent;
    }

    private static bool IsShellSurfaceProcess(string? processName)
        => !string.IsNullOrWhiteSpace(processName) &&
           ShellSurfaceProcesses.Contains(processName);

    private static bool HasSameProcess(SystemContextSnapshot expected, SystemContextSnapshot current)
        => expected.ForegroundProcessId > 0 &&
           expected.ForegroundProcessId == current.ForegroundProcessId &&
           expected.ForegroundProcess.Equals(current.ForegroundProcess, StringComparison.OrdinalIgnoreCase);

    public static bool HasSameIdentity(SystemContextSnapshot expected, SystemContextSnapshot current)
    {
        if (expected.ForegroundProcessId <= 0 || current.ForegroundProcessId <= 0) return false;
        if (expected.ForegroundProcessId != current.ForegroundProcessId) return false;
        if (expected.ForegroundWindowHandle == nint.Zero || current.ForegroundWindowHandle == nint.Zero) return false;
        if (expected.ForegroundWindowHandle != current.ForegroundWindowHandle) return false;
        return expected.ForegroundProcess.Equals(current.ForegroundProcess, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasUsableForeground(SystemContextSnapshot context)
        => context.ForegroundProcessId > 0 &&
           context.ForegroundWindowHandle != nint.Zero &&
           !string.IsNullOrWhiteSpace(context.ForegroundProcess);

    private static string BuildFingerprint(
        SystemContextSnapshot system,
        IReadOnlyList<UiElementCandidate> elements)
    {
        var browser = system.Browser?.Domain ?? string.Empty;
        var stableElements = elements
            .Where(x => x.Interactable)
            .OrderByDescending(x => x.Focused)
            .ThenBy(x => x.ProcessId)
            .ThenBy(x => x.ControlType, StringComparer.Ordinal)
            .ThenBy(x => x.AutomationId, StringComparer.Ordinal)
            .Take(96)
            .Select(x =>
                $"{x.ProcessId}:{x.ControlType}:{x.AutomationId}:{x.Name}:{Math.Round(x.X / 24d)},{Math.Round(x.Y / 24d)}:{x.ToggleState}:{x.Selected}:{x.ExpandCollapseState}");

        return $"{system.ForegroundProcessId}|{system.ForegroundWindowHandle}|{browser}|{string.Join("\n", stableElements)}";
    }
}

public sealed class ObservationChangedException : Exception
{
    public ObservationChangedException(string message) : base(message) { }
}

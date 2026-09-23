using HelpSys.Models;

namespace HelpSys.Services;

public sealed class ObservationBroker
{
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

        var after = _systemContext.Capture();
        if (!HasSameIdentity(before, after))
            throw new ObservationChangedException("UIA取得中に前面ウィンドウが変化しました。");

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

using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using HelpSys.Models;

namespace HelpSys.Services;

public sealed class UiAutomationScanner
{
    private static readonly string[] ShellSurfaceProcesses = ["explorer", "SearchHost", "StartMenuExperienceHost"];
    private static readonly Lazy<UiAutomationObserverClient> SharedObserver = new(() => new UiAutomationObserverClient());
    private readonly int _selfProcessId = Environment.ProcessId;
    private readonly bool _forceLocal;

    public UiAutomationScanner() : this(forceLocal: false) { }

    internal UiAutomationScanner(bool forceLocal)
    {
        _forceLocal = forceLocal;
    }

    private bool UseObserver => !_forceLocal && !UiAutomationObserverHost.IsObserverProcess;

    public static void ShutdownSharedObserver()
    {
        if (SharedObserver.IsValueCreated) SharedObserver.Value.Dispose();
    }

    public Task<IReadOnlyList<UiElementCandidate>> CaptureCandidatesAsync(int maxCandidates = 360, CancellationToken cancellationToken = default)
        => UseObserver
            ? SharedObserver.Value.CaptureCandidatesAsync(maxCandidates, cancellationToken)
            : Task.FromResult(CaptureCandidates(maxCandidates, cancellationToken));

    public Task<IReadOnlyList<UiElementCandidate>> CaptureCandidatesForProcessAsync(int processId, int maxCandidates = 360, CancellationToken cancellationToken = default)
    {
        if (processId <= 0) return Task.FromResult<IReadOnlyList<UiElementCandidate>>([]);
        return UseObserver
            ? SharedObserver.Value.CaptureCandidatesForProcessAsync(processId, maxCandidates, cancellationToken)
            : Task.FromResult(CaptureCandidates(maxCandidates, cancellationToken, processId));
    }

    public Task<string> CaptureWindowDiagnosticsAsync(
        nint windowHandle,
        int expectedProcessId,
        CancellationToken cancellationToken = default)
    {
        if (windowHandle == nint.Zero) return Task.FromResult("hwnd=missing");
        return UseObserver
            ? SharedObserver.Value.CaptureWindowDiagnosticsAsync(windowHandle, expectedProcessId, cancellationToken)
            : Task.FromResult(CaptureWindowDiagnostics(windowHandle, expectedProcessId, cancellationToken));
    }

    private static string CaptureWindowDiagnostics(nint windowHandle, int expectedProcessId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var root = AutomationElement.FromHandle((IntPtr)windowHandle);
        if (root is null) return $"expectedPid={expectedProcessId};root=missing";

        var walker = TreeWalker.ControlViewWalker;
        var queue = new Queue<(AutomationElement Element, int Depth)>();
        queue.Enqueue((root, 0));
        var visited = 0;
        var visible = 0;
        var enabled = 0;
        var focusable = 0;
        var knownInteractive = 0;
        var unclassifiedFocusable = 0;
        var processIds = new HashSet<int>();
        var controlCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var stopwatch = Stopwatch.StartNew();

        while (queue.Count > 0 && visited < 7500 && stopwatch.Elapsed < TimeSpan.FromMilliseconds(2200))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (element, depth) = queue.Dequeue();
            visited++;
            try
            {
                var current = element.Current;
                if (current.ProcessId > 0) processIds.Add(current.ProcessId);
                var typeName = current.ControlType?.ProgrammaticName ?? "ControlType.Unknown";
                controlCounts[typeName] = controlCounts.TryGetValue(typeName, out var count) ? count + 1 : 1;

                var isVisible = !current.IsOffscreen && !current.BoundingRectangle.IsEmpty;
                if (isVisible) visible++;
                if (current.IsEnabled) enabled++;
                if (current.IsKeyboardFocusable) focusable++;
                var isKnownInteractive = IsInteractiveType(typeName);
                if (isKnownInteractive) knownInteractive++;
                if (isVisible && current.IsEnabled && current.IsKeyboardFocusable && !isKnownInteractive)
                    unclassifiedFocusable++;

                if (depth < 12) EnqueueChildren(walker, element, depth + 1, queue);
            }
            catch (ElementNotAvailableException) { }
            catch (InvalidOperationException) { }
        }

        var pidSummary = processIds.Count == 0
            ? "none"
            : string.Join(',', processIds.OrderBy(x => x));
        var typeSummary = string.Join(',', controlCounts
            .OrderByDescending(x => x.Value)
            .ThenBy(x => x.Key, StringComparer.Ordinal)
            .Take(16)
            .Select(x => $"{x.Key.Replace("ControlType.", string.Empty, StringComparison.Ordinal)}:{x.Value}"));

        return $"expectedPid={expectedProcessId};pids={pidSummary};crossProcess={processIds.Any(x => x != expectedProcessId)};visited={visited};visible={visible};enabled={enabled};focusable={focusable};knownInteractive={knownInteractive};unclassifiedFocusable={unclassifiedFocusable};types={typeSummary}";
    }

    public Task<UiElementCandidate?> RevalidateCandidateAsync(UiElementCandidate candidate, CancellationToken cancellationToken = default)
        => UseObserver
            ? SharedObserver.Value.RevalidateCandidateAsync(candidate, null, cancellationToken)
            : Task.FromResult(RevalidateCandidate(candidate, null, cancellationToken));

    public Task<UiElementCandidate?> RevalidateCandidateAsync(UiElementCandidate candidate, int rootProcessId, CancellationToken cancellationToken = default)
        => UseObserver
            ? SharedObserver.Value.RevalidateCandidateAsync(candidate, rootProcessId > 0 ? rootProcessId : null, cancellationToken)
            : Task.FromResult(RevalidateCandidate(candidate, rootProcessId > 0 ? rootProcessId : null, cancellationToken));

    public Task<Rect?> SnapToAccessibleBoundsAsync(Rect approximateBounds, CancellationToken cancellationToken = default)
        => UseObserver
            ? SharedObserver.Value.SnapToAccessibleBoundsAsync(approximateBounds, cancellationToken)
            : Task.FromResult(SnapToAccessibleBounds(approximateBounds, cancellationToken));

    public Task<UiElementCandidate?> SnapToAccessibleCandidateAsync(Rect approximateBounds, CancellationToken cancellationToken = default)
        => UseObserver
            ? SharedObserver.Value.SnapToAccessibleCandidateAsync(approximateBounds, cancellationToken)
            : Task.FromResult(SnapToAccessibleCandidate(approximateBounds, cancellationToken));

    private UiElementCandidate? RevalidateCandidate(UiElementCandidate candidate, int? rootProcessId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var direct = TryRevalidateNearOriginalBounds(candidate, rootProcessId, cancellationToken);
        if (direct is not null) return direct;

        // Rare fallback only. Do not rescan hundreds of candidates for every normal click.
        var current = CaptureCandidates(240, cancellationToken, rootProcessId).Where(x => x.Interactable).ToArray();
        UiElementCandidate? best = null;
        var bestScore = double.NegativeInfinity;
        var oldCenterX = candidate.X + candidate.Width / 2d;
        var oldCenterY = candidate.Y + candidate.Height / 2d;

        var automationIdMultiplicity = string.IsNullOrWhiteSpace(candidate.AutomationId)
            ? 0
            : current.Count(x =>
                x.ControlType.Equals(candidate.ControlType, StringComparison.OrdinalIgnoreCase) &&
                x.AutomationId.Equals(candidate.AutomationId, StringComparison.Ordinal) &&
                (candidate.ProcessId > 0
                    ? x.ProcessId == candidate.ProcessId
                    : x.ProcessName.Equals(candidate.ProcessName, StringComparison.OrdinalIgnoreCase)));

        foreach (var item in current)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var sameProcessId = candidate.ProcessId > 0 && item.ProcessId == candidate.ProcessId;
            var sameProcessName = !string.IsNullOrWhiteSpace(candidate.ProcessName) &&
                                  item.ProcessName.Equals(candidate.ProcessName, StringComparison.OrdinalIgnoreCase);

            if (candidate.ProcessId > 0)
            {
                if (!sameProcessId) continue;
            }
            else if (!string.IsNullOrWhiteSpace(candidate.ProcessName) && !sameProcessName)
            {
                continue;
            }

            var sameType = item.ControlType.Equals(candidate.ControlType, StringComparison.OrdinalIgnoreCase);
            if (!sameType) continue;

            var exactAutomationId = !string.IsNullOrWhiteSpace(candidate.AutomationId) &&
                                    item.AutomationId.Equals(candidate.AutomationId, StringComparison.Ordinal);
            var exactName = !string.IsNullOrWhiteSpace(candidate.Name) &&
                            item.Name.Equals(candidate.Name, StringComparison.OrdinalIgnoreCase);
            var exactClass = !string.IsNullOrWhiteSpace(candidate.ClassName) &&
                             item.ClassName.Equals(candidate.ClassName, StringComparison.Ordinal);

            var centerX = item.X + item.Width / 2d;
            var centerY = item.Y + item.Height / 2d;
            var distance = Math.Sqrt(Math.Pow(centerX - oldCenterX, 2) + Math.Pow(centerY - oldCenterY, 2));
            var oldRect = candidate.Bounds;
            var overlap = oldRect.IntersectsWith(item.Bounds) ? Rect.Intersect(oldRect, item.Bounds) : Rect.Empty;
            var overlapArea = overlap.IsEmpty ? 0d : overlap.Width * overlap.Height;
            var smallerArea = Math.Max(1d, Math.Min(oldRect.Width * oldRect.Height, item.Width * item.Height));
            var overlapRatio = overlapArea / smallerArea;
            var positionalIdentity = exactClass && distance <= 65 && overlapRatio >= 0.35;

            if (exactAutomationId && automationIdMultiplicity > 1 && !exactName && !positionalIdentity) continue;
            if (!exactAutomationId && !exactName && !positionalIdentity) continue;

            var score = 0d;
            if (sameProcessId) score += 35;
            else if (sameProcessName) score += 15;
            if (exactAutomationId) score += automationIdMultiplicity > 1 ? 95 : 190;
            if (exactName) score += 145;
            if (exactClass) score += 35;
            score += 45;
            score += Math.Min(90, overlapRatio * 90);
            score -= Math.Min(120, distance / 7d);

            if (score > bestScore)
            {
                bestScore = score;
                best = item;
            }
        }

        return bestScore >= 125 ? best : null;
    }

    private UiElementCandidate? TryRevalidateNearOriginalBounds(
        UiElementCandidate candidate,
        int? rootProcessId,
        CancellationToken cancellationToken)
    {
        if (candidate.Bounds.IsEmpty) return null;

        var points = new[]
        {
            new Point(candidate.X + candidate.Width / 2d, candidate.Y + candidate.Height / 2d),
            new Point(candidate.X + candidate.Width * 0.25, candidate.Y + candidate.Height * 0.5),
            new Point(candidate.X + candidate.Width * 0.75, candidate.Y + candidate.Height * 0.5)
        };

        foreach (var point in points)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var element = AutomationElement.FromPoint(point);
                var walker = TreeWalker.ControlViewWalker;
                for (var depth = 0; element is not null && depth < 7; depth++)
                {
                    var current = element.Current;
                    if (current.ProcessId == _selfProcessId)
                    {
                        element = walker.GetParent(element);
                        continue;
                    }

                    var expectedPid = rootProcessId.GetValueOrDefault(candidate.ProcessId);
                    if (expectedPid > 0 && current.ProcessId != expectedPid)
                    {
                        element = walker.GetParent(element);
                        continue;
                    }

                    var typeName = (current.ControlType?.ProgrammaticName ?? string.Empty)
                        .Replace("ControlType.", string.Empty, StringComparison.Ordinal);
                    if (!typeName.Equals(candidate.ControlType, StringComparison.OrdinalIgnoreCase))
                    {
                        element = walker.GetParent(element);
                        continue;
                    }

                    var automationId = current.AutomationId ?? string.Empty;
                    var name = current.Name ?? string.Empty;
                    var className = current.ClassName ?? string.Empty;
                    var identityMatch =
                        (!string.IsNullOrWhiteSpace(candidate.AutomationId) &&
                         automationId.Equals(candidate.AutomationId.Split("|role:", 2, StringSplitOptions.None)[0], StringComparison.Ordinal)) ||
                        (!string.IsNullOrWhiteSpace(candidate.Name) &&
                         name.Equals(candidate.Name, StringComparison.OrdinalIgnoreCase)) ||
                        (!string.IsNullOrWhiteSpace(candidate.ClassName) &&
                         className.Equals(candidate.ClassName, StringComparison.Ordinal));

                    if (!identityMatch)
                    {
                        element = walker.GetParent(element);
                        continue;
                    }

                    var rect = current.BoundingRectangle;
                    if (rect.IsEmpty || !current.IsEnabled || current.IsOffscreen) return null;
                    var rebuilt = BuildSnapCandidate(
                        element,
                        current,
                        current.ControlType?.ProgrammaticName ?? string.Empty,
                        rect);
                    return rebuilt with { Id = candidate.Id };
                }
            }
            catch (ElementNotAvailableException) { }
            catch (InvalidOperationException) { }
        }

        return null;
    }

    private Rect? SnapToAccessibleBounds(Rect approximateBounds, CancellationToken cancellationToken)
        => SnapToAccessibleCandidate(approximateBounds, cancellationToken)?.Bounds;

    private UiElementCandidate? SnapToAccessibleCandidate(Rect approximateBounds, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (approximateBounds.IsEmpty) return null;

        var center = new Point(approximateBounds.Left + approximateBounds.Width / 2d, approximateBounds.Top + approximateBounds.Height / 2d);
        var points = new[]
        {
            center,
            new Point(approximateBounds.Left + approximateBounds.Width * 0.25, approximateBounds.Top + approximateBounds.Height * 0.5),
            new Point(approximateBounds.Left + approximateBounds.Width * 0.75, approximateBounds.Top + approximateBounds.Height * 0.5),
            new Point(approximateBounds.Left + approximateBounds.Width * 0.5, approximateBounds.Top + approximateBounds.Height * 0.25),
            new Point(approximateBounds.Left + approximateBounds.Width * 0.5, approximateBounds.Top + approximateBounds.Height * 0.75)
        };

        int visibleProcessId = 0;

        foreach (var point in points)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var element = AutomationElement.FromPoint(point);
                var walker = TreeWalker.ControlViewWalker;
                for (var i = 0; element is not null && i < 8; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var current = element.Current;
                    if (visibleProcessId == 0 && current.ProcessId != _selfProcessId) visibleProcessId = current.ProcessId;
                    var typeName = current.ControlType?.ProgrammaticName ?? string.Empty;
                    var rect = current.BoundingRectangle;
                    if (current.ProcessId != _selfProcessId && current.IsEnabled && !current.IsOffscreen && IsInteractiveType(typeName) &&
                        !rect.IsEmpty && rect.Width >= 8 && rect.Height >= 8)
                    {
                        var inflated = approximateBounds;
                        inflated.Inflate(Math.Max(25, approximateBounds.Width * 0.45), Math.Max(25, approximateBounds.Height * 0.45));
                        if (inflated.IntersectsWith(rect) || rect.Contains(point))
                            return BuildSnapCandidate(element, current, typeName, rect);
                    }
                    element = walker.GetParent(element);
                }
            }
            catch (ElementNotAvailableException) { }
            catch (InvalidOperationException) { }
        }

        if (visibleProcessId <= 0 || visibleProcessId == _selfProcessId) return null;

        var candidates = CaptureCandidates(240, cancellationToken)
            .Where(x => x.Interactable && !x.Bounds.IsEmpty && x.ProcessId == visibleProcessId)
            .ToArray();
        UiElementCandidate? best = null;
        var bestScore = double.NegativeInfinity;

        foreach (var item in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rect = item.Bounds;
            var intersection = approximateBounds.IntersectsWith(rect) ? Rect.Intersect(approximateBounds, rect) : Rect.Empty;
            var intersectionArea = intersection.IsEmpty ? 0d : intersection.Width * intersection.Height;
            var smallerArea = Math.Max(1d, Math.Min(approximateBounds.Width * approximateBounds.Height, rect.Width * rect.Height));
            var overlapRatio = intersectionArea / smallerArea;
            var itemCenter = new Point(rect.Left + rect.Width / 2d, rect.Top + rect.Height / 2d);
            var distance = Math.Sqrt(Math.Pow(itemCenter.X - center.X, 2) + Math.Pow(itemCenter.Y - center.Y, 2));

            if (overlapRatio < 0.10 && distance > Math.Max(55, Math.Min(approximateBounds.Width, approximateBounds.Height) * 0.9)) continue;

            var score = overlapRatio * 180d - Math.Min(120, distance / 4d);
            if (rect.Contains(center)) score += 75;
            if (item.ControlType is "Button" or "ListItem" or "MenuItem" or "Hyperlink" or "TabItem") score += 20;
            if (score > bestScore)
            {
                bestScore = score;
                best = item;
            }
        }

        return best is not null && bestScore >= 25 ? best : null;
    }

    private static UiElementCandidate BuildSnapCandidate(
        AutomationElement element,
        AutomationElement.AutomationElementInformation current,
        string typeName,
        Rect rect)
    {
        var isPassword = current.IsPassword;
        var actionState = ReadActionState(element, typeName, isPassword, cached: false);
        string processName;
        try { processName = Process.GetProcessById(current.ProcessId).ProcessName; }
        catch { processName = string.Empty; }

        var rawName = current.Name ?? string.Empty;
        var name = isPassword ? "[password field]" : rawName;
        return new UiElementCandidate(
            "snap-target",
            Trim(name, 180),
            Trim(current.AutomationId ?? string.Empty, 120),
            Trim(current.ClassName ?? string.Empty, 120),
            Trim(typeName.Replace("ControlType.", string.Empty), 80),
            processName,
            true,
            current.IsEnabled,
            current.IsKeyboardFocusable,
            current.HasKeyboardFocus,
            isPassword,
            rect.X,
            rect.Y,
            rect.Width,
            rect.Height,
            current.ProcessId,
            actionState.Value,
            actionState.ToggleState,
            actionState.Selected,
            actionState.ExpandCollapseState);
    }

    private IReadOnlyList<UiElementCandidate> CaptureCandidates(int maxCandidates, CancellationToken cancellationToken, int? rootProcessId = null)
    {
        var root = AutomationElement.RootElement;
        var walker = TreeWalker.ControlViewWalker;
        var cacheRequest = CreateTraversalCacheRequest();
        var queue = new Queue<(AutomationElement Element, int Depth, bool Cached)>();
        if (rootProcessId is > 0) EnqueueProcessSurfaceRoots(rootProcessId.Value, queue);
        else EnqueueChildrenCached(walker, root, 0, queue, cacheRequest);

        var interactivePoolLimit = Math.Max(360, maxCandidates * 2);
        var contextPoolLimit = Math.Max(90, maxCandidates / 3);
        var interactive = new List<UiElementCandidate>(interactivePoolLimit);
        var context = new List<UiElementCandidate>(contextPoolLimit);
        var processNames = new Dictionary<int, string>();
        var visited = 0;
        var stopwatch = Stopwatch.StartNew();

        while (queue.Count > 0 && visited < 4500 && stopwatch.ElapsedMilliseconds < 1400)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (element, depth, cached) = queue.Dequeue();
            visited++;

            try
            {
                var current = cached ? element.Cached : element.Current;
                if (current.ProcessId != _selfProcessId && !current.IsOffscreen && current.IsEnabled)
                {
                    var rect = current.BoundingRectangle;
                    var typeName = current.ControlType?.ProgrammaticName ?? string.Empty;
                    var isPassword = current.IsPassword;
                    var isInput = typeName.EndsWith("Edit", StringComparison.Ordinal) || typeName.EndsWith("ComboBox", StringComparison.Ordinal);
                    var rawName = current.Name ?? string.Empty;

                    // Name/AutomationId/ClassName are already fetched in the traversal cache.
                    // Avoid a second TextPattern roundtrip for every context node.
                    var name = isPassword ? "[password field]" : isInput ? "[input field]" : NormalizeReadableText(rawName, 420);
                    var automationId = current.AutomationId ?? string.Empty;
                    var className = current.ClassName ?? string.Empty;
                    var processName = GetProcessName(current.ProcessId, processNames);
                    if (isInput && !isPassword)
                        automationId = AnnotateInputSemanticRole(automationId, rawName, className, processName);
                    var isInteractive = IsInteractiveType(typeName);
                    var isContext = !isInteractive && IsContextType(typeName, name, rect);

                    if (isInteractive && interactive.Count < interactivePoolLimit && ShouldKeep(name, automationId, className, rect))
                    {
                        var actionState = ReadActionState(element, typeName, isPassword, cached);
                        interactive.Add(new UiElementCandidate(
                            $"u{interactive.Count + 1}", Trim(name, 180), Trim(automationId, 120), Trim(className, 120),
                            Trim(typeName.Replace("ControlType.", string.Empty), 80), processName,
                            true, current.IsEnabled, current.IsKeyboardFocusable, current.HasKeyboardFocus, isPassword,
                            rect.X, rect.Y, rect.Width, rect.Height, current.ProcessId,
                            actionState.Value, actionState.ToggleState, actionState.Selected, actionState.ExpandCollapseState));
                    }
                    else if (isContext && context.Count < contextPoolLimit)
                    {
                        context.Add(new UiElementCandidate(
                            $"c{context.Count + 1}", Trim(name, 420), Trim(automationId, 120), Trim(className, 120),
                            Trim(typeName.Replace("ControlType.", string.Empty), 80), processName,
                            false, current.IsEnabled, current.IsKeyboardFocusable, current.HasKeyboardFocus, isPassword,
                            rect.X, rect.Y, rect.Width, rect.Height, current.ProcessId));
                    }
                }
            }
            catch (ElementNotAvailableException) { }
            catch (InvalidOperationException) { }

            if (depth < 10) EnqueueChildrenCached(walker, element, depth + 1, queue, cacheRequest);
        }

        var contextBudget = Math.Min(context.Count, Math.Max(30, maxCandidates / 7));
        var interactiveBudget = Math.Max(0, maxCandidates - contextBudget);
        var rankedInteractive = interactive
            .OrderByDescending(CandidatePriority)
            .Take(interactiveBudget)
            .ToArray();
        var rankedContext = context
            .OrderByDescending(CandidatePriority)
            .Take(maxCandidates - rankedInteractive.Length)
            .ToArray();

        return rankedInteractive.Concat(rankedContext).ToArray();
    }

    private static CacheRequest CreateTraversalCacheRequest()
    {
        var request = new CacheRequest { TreeScope = TreeScope.Element };
        request.Add(AutomationElement.ProcessIdProperty);
        request.Add(AutomationElement.NameProperty);
        request.Add(AutomationElement.AutomationIdProperty);
        request.Add(AutomationElement.ClassNameProperty);
        request.Add(AutomationElement.ControlTypeProperty);
        request.Add(AutomationElement.BoundingRectangleProperty);
        request.Add(AutomationElement.IsEnabledProperty);
        request.Add(AutomationElement.IsOffscreenProperty);
        request.Add(AutomationElement.IsKeyboardFocusableProperty);
        request.Add(AutomationElement.HasKeyboardFocusProperty);
        request.Add(AutomationElement.IsPasswordProperty);
        request.Add(ValuePattern.Pattern);
        request.Add(TogglePattern.Pattern);
        request.Add(SelectionItemPattern.Pattern);
        request.Add(ExpandCollapsePattern.Pattern);
        return request;
    }

    private static string NormalizeReadableText(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var normalized = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= max ? normalized : normalized[..max];
    }

    private static ActionState ReadActionState(
        AutomationElement element,
        string typeName,
        bool isPassword,
        bool cached)
    {
        string? value = null;
        string? toggleState = null;
        bool? selected = null;
        string? expandCollapseState = null;

        try
        {
            if (!isPassword && (typeName.EndsWith("Edit", StringComparison.Ordinal) || typeName.EndsWith("ComboBox", StringComparison.Ordinal)))
            {
                ValuePattern? pattern = cached
                    ? element.GetCachedPattern(ValuePattern.Pattern) as ValuePattern
                    : element.TryGetCurrentPattern(ValuePattern.Pattern, out var currentPattern) ? currentPattern as ValuePattern : null;
                var rawValue = cached ? pattern?.Cached.Value?.Trim() : pattern?.Current.Value?.Trim();
                if (!string.IsNullOrWhiteSpace(rawValue))
                    value = rawValue.Length <= 320 ? rawValue : rawValue[..320];
            }
        }
        catch (ElementNotAvailableException) { }
        catch (InvalidOperationException) { }
        catch (NotSupportedException) { }

        try
        {
            if (typeName.EndsWith("CheckBox", StringComparison.Ordinal) || typeName.EndsWith("RadioButton", StringComparison.Ordinal))
            {
                TogglePattern? pattern = cached
                    ? element.GetCachedPattern(TogglePattern.Pattern) as TogglePattern
                    : element.TryGetCurrentPattern(TogglePattern.Pattern, out var currentPattern) ? currentPattern as TogglePattern : null;
                if (pattern is not null)
                    toggleState = (cached ? pattern.Cached.ToggleState : pattern.Current.ToggleState).ToString();
            }
        }
        catch (ElementNotAvailableException) { }
        catch (InvalidOperationException) { }
        catch (NotSupportedException) { }

        try
        {
            SelectionItemPattern? pattern = cached
                ? element.GetCachedPattern(SelectionItemPattern.Pattern) as SelectionItemPattern
                : element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var currentPattern) ? currentPattern as SelectionItemPattern : null;
            if (pattern is not null)
                selected = cached ? pattern.Cached.IsSelected : pattern.Current.IsSelected;
        }
        catch (ElementNotAvailableException) { }
        catch (InvalidOperationException) { }
        catch (NotSupportedException) { }

        try
        {
            ExpandCollapsePattern? pattern = cached
                ? element.GetCachedPattern(ExpandCollapsePattern.Pattern) as ExpandCollapsePattern
                : element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var currentPattern) ? currentPattern as ExpandCollapsePattern : null;
            if (pattern is not null)
                expandCollapseState = (cached ? pattern.Cached.ExpandCollapseState : pattern.Current.ExpandCollapseState).ToString();
        }
        catch (ElementNotAvailableException) { }
        catch (InvalidOperationException) { }
        catch (NotSupportedException) { }

        return new ActionState(value, toggleState, selected, expandCollapseState);
    }

    private static string AnnotateInputSemanticRole(string automationId, string rawName, string className, string processName)
    {
        var role = ClassifyInputSemanticRole(automationId, rawName, className, processName);
        if (string.IsNullOrEmpty(role)) return automationId;
        var baseId = Trim(automationId, 88);
        return string.IsNullOrWhiteSpace(baseId) ? $"role:{role}" : $"{baseId}|role:{role}";
    }

    private static string? ClassifyInputSemanticRole(string automationId, string rawName, string className, string processName)
    {
        var process = processName ?? string.Empty;
        var hint = $"{rawName} {automationId} {className}";

        if ((process.Contains("SearchHost", StringComparison.OrdinalIgnoreCase) ||
             process.Contains("StartMenuExperienceHost", StringComparison.OrdinalIgnoreCase) ||
             process.Equals("explorer", StringComparison.OrdinalIgnoreCase)) &&
            (hint.Contains("search", StringComparison.OrdinalIgnoreCase) || hint.Contains("検索", StringComparison.OrdinalIgnoreCase)))
            return "windows_search";

        var browser = process.Equals("chrome", StringComparison.OrdinalIgnoreCase) ||
                      process.Equals("msedge", StringComparison.OrdinalIgnoreCase) ||
                      process.Equals("firefox", StringComparison.OrdinalIgnoreCase) ||
                      process.Equals("brave", StringComparison.OrdinalIgnoreCase) ||
                      process.Equals("opera", StringComparison.OrdinalIgnoreCase) ||
                      process.Equals("vivaldi", StringComparison.OrdinalIgnoreCase);
        if (!browser) return null;

        if (hint.Contains("address", StringComparison.OrdinalIgnoreCase) ||
            hint.Contains("location", StringComparison.OrdinalIgnoreCase) ||
            hint.Contains("omnibox", StringComparison.OrdinalIgnoreCase) ||
            hint.Contains("urlbar", StringComparison.OrdinalIgnoreCase) ||
            hint.Contains("url bar", StringComparison.OrdinalIgnoreCase) ||
            hint.Contains("web address", StringComparison.OrdinalIgnoreCase) ||
            hint.Contains("location bar", StringComparison.OrdinalIgnoreCase) ||
            hint.Contains("アドレス", StringComparison.OrdinalIgnoreCase) ||
            hint.Contains("URL", StringComparison.OrdinalIgnoreCase))
            return "browser_address";

        if (hint.Contains("search", StringComparison.OrdinalIgnoreCase) ||
            hint.Contains("検索", StringComparison.OrdinalIgnoreCase) ||
            hint.Contains("query", StringComparison.OrdinalIgnoreCase))
            return "web_search";

        return null;
    }

    private static double CandidatePriority(UiElementCandidate item)
    {
        var score = item.Interactable ? 500d : 0d;
        if (item.Focused) score += 1000;
        var process = item.ProcessName ?? string.Empty;
        if (process.Contains("StartMenuExperienceHost", StringComparison.OrdinalIgnoreCase) ||
            process.Contains("SearchHost", StringComparison.OrdinalIgnoreCase)) score += 850;
        if (process.Equals("explorer", StringComparison.OrdinalIgnoreCase))
        {
            if (item.ControlType is "Button" or "MenuItem") score += 430;
            else if (item.ControlType == "ListItem") score += 260;
            else score += 120;
        }
        if (process is "chrome" or "msedge" or "firefox" or "brave" or "opera" or "vivaldi") score += 320;
        if (process.Equals("excel", StringComparison.OrdinalIgnoreCase) ||
            process.Equals("winword", StringComparison.OrdinalIgnoreCase) ||
            process.Equals("powerpnt", StringComparison.OrdinalIgnoreCase)) score += 240;
        if (item.ControlType is "Document" or "Text" or "DataItem" or "Hyperlink") score += 180;
        if (item.ControlType == "Edit") score += 160;
        if (!string.IsNullOrWhiteSpace(item.Name)) score += 45;
        return score;
    }

    private static bool IsInteractiveType(string typeName) =>
        typeName.EndsWith("Button", StringComparison.Ordinal) || typeName.EndsWith("ListItem", StringComparison.Ordinal) ||
        typeName.EndsWith("MenuItem", StringComparison.Ordinal) || typeName.EndsWith("Hyperlink", StringComparison.Ordinal) ||
        typeName.EndsWith("TabItem", StringComparison.Ordinal) || typeName.EndsWith("Edit", StringComparison.Ordinal) ||
        typeName.EndsWith("ComboBox", StringComparison.Ordinal) || typeName.EndsWith("CheckBox", StringComparison.Ordinal) ||
        typeName.EndsWith("RadioButton", StringComparison.Ordinal) || typeName.EndsWith("TreeItem", StringComparison.Ordinal);

    private static bool IsContextType(string typeName, string name, Rect rect)
    {
        if (rect.IsEmpty || rect.Width < 8 || rect.Height < 8 || string.IsNullOrWhiteSpace(name)) return false;
        return typeName.EndsWith("Text", StringComparison.Ordinal) || typeName.EndsWith("Window", StringComparison.Ordinal) ||
               typeName.EndsWith("Pane", StringComparison.Ordinal) || typeName.EndsWith("Group", StringComparison.Ordinal) ||
               typeName.EndsWith("TitleBar", StringComparison.Ordinal) || typeName.EndsWith("Document", StringComparison.Ordinal) ||
               typeName.EndsWith("DataItem", StringComparison.Ordinal) || typeName.EndsWith("Table", StringComparison.Ordinal) ||
               typeName.EndsWith("Header", StringComparison.Ordinal) || typeName.EndsWith("HeaderItem", StringComparison.Ordinal) ||
               typeName.EndsWith("List", StringComparison.Ordinal);
    }

    private static bool ShouldKeep(string name, string automationId, string className, Rect rect)
    {
        if (rect.IsEmpty || rect.Width < 8 || rect.Height < 8) return false;
        return !string.IsNullOrWhiteSpace(name) || !string.IsNullOrWhiteSpace(automationId) || !string.IsNullOrWhiteSpace(className);
    }

    private static string GetProcessName(int processId, Dictionary<int, string> cache)
    {
        if (processId <= 0) return string.Empty;
        if (cache.TryGetValue(processId, out var cached)) return cached;
        try { cached = Process.GetProcessById(processId).ProcessName; } catch { cached = string.Empty; }
        cache[processId] = cached;
        return cached;
    }

    private static void EnqueueProcessSurfaceRoots(int processId, Queue<(AutomationElement Element, int Depth, bool Cached)> queue)
    {
        EnqueueProcessRoots(processId, queue);

        string rootProcessName;
        try { rootProcessName = Process.GetProcessById(processId).ProcessName; }
        catch { return; }
        if (!ShellSurfaceProcesses.Contains(rootProcessName, StringComparer.OrdinalIgnoreCase)) return;

        foreach (var name in ShellSurfaceProcesses)
        {
            Process[] related;
            try { related = Process.GetProcessesByName(name); }
            catch { continue; }

            foreach (var process in related)
            {
                using (process)
                {
                    try
                    {
                        if (process.Id != processId) EnqueueProcessRoots(process.Id, queue);
                    }
                    catch { }
                }
            }
        }
    }

    private static string Trim(string value, int max) => value.Length <= max ? value : value[..max];

    private static void EnqueueProcessRoots(
        int processId,
        Queue<(AutomationElement Element, int Depth, bool Cached)> queue)
    {
        try
        {
            var condition = new PropertyCondition(AutomationElement.ProcessIdProperty, processId);
            var roots = AutomationElement.RootElement.FindAll(TreeScope.Children, condition);
            foreach (AutomationElement root in roots) queue.Enqueue((root, 0, false));
        }
        catch (ElementNotAvailableException) { }
        catch (InvalidOperationException) { }
    }

    private static void EnqueueChildren(
        TreeWalker walker,
        AutomationElement parent,
        int depth,
        Queue<(AutomationElement Element, int Depth)> queue)
    {
        try
        {
            var child = walker.GetFirstChild(parent);
            while (child is not null)
            {
                queue.Enqueue((child, depth));
                child = walker.GetNextSibling(child);
            }
        }
        catch (ElementNotAvailableException) { }
        catch (InvalidOperationException) { }
    }

    private static void EnqueueChildrenCached(
        TreeWalker walker,
        AutomationElement parent,
        int depth,
        Queue<(AutomationElement Element, int Depth, bool Cached)> queue,
        CacheRequest cacheRequest)
    {
        try
        {
            var child = walker.GetFirstChild(parent, cacheRequest);
            while (child is not null)
            {
                queue.Enqueue((child, depth, true));
                child = walker.GetNextSibling(child, cacheRequest);
            }
        }
        catch (ElementNotAvailableException) { }
        catch (InvalidOperationException) { }
    }

    private readonly record struct ActionState(string? Value, string? ToggleState, bool? Selected, string? ExpandCollapseState);
}
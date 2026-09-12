using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using HelpSys.Models;

namespace HelpSys.Services;

public sealed class UiAutomationScanner
{
    private static readonly string[] ShellSurfaceProcesses = ["explorer", "SearchHost", "StartMenuExperienceHost"];
    private readonly int _selfProcessId = Environment.ProcessId;

    public Task<IReadOnlyList<UiElementCandidate>> CaptureCandidatesAsync(int maxCandidates = 360, CancellationToken cancellationToken = default)
        => Task.Run(() => CaptureCandidates(maxCandidates, cancellationToken), cancellationToken);

    public Task<IReadOnlyList<UiElementCandidate>> CaptureCandidatesForProcessAsync(int processId, int maxCandidates = 360, CancellationToken cancellationToken = default)
    {
        if (processId <= 0) return Task.FromResult<IReadOnlyList<UiElementCandidate>>([]);
        return Task.Run(() => CaptureCandidates(maxCandidates, cancellationToken, processId), cancellationToken);
    }

    public Task<UiElementCandidate?> RevalidateCandidateAsync(UiElementCandidate candidate, CancellationToken cancellationToken = default)
        => Task.Run(() => RevalidateCandidate(candidate, null, cancellationToken), cancellationToken);

    public Task<UiElementCandidate?> RevalidateCandidateAsync(UiElementCandidate candidate, int rootProcessId, CancellationToken cancellationToken = default)
        => Task.Run(() => RevalidateCandidate(candidate, rootProcessId > 0 ? rootProcessId : null, cancellationToken), cancellationToken);

    public Task<Rect?> SnapToAccessibleBoundsAsync(Rect approximateBounds, CancellationToken cancellationToken = default)
        => Task.Run(() => SnapToAccessibleBounds(approximateBounds, cancellationToken), cancellationToken);

    private UiElementCandidate? RevalidateCandidate(UiElementCandidate candidate, int? rootProcessId, CancellationToken cancellationToken)
    {
        var current = CaptureCandidates(700, cancellationToken, rootProcessId).Where(x => x.Interactable).ToArray();
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

    private Rect? SnapToAccessibleBounds(Rect approximateBounds, CancellationToken cancellationToken)
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
                        if (inflated.IntersectsWith(rect) || rect.Contains(point)) return rect;
                    }
                    element = walker.GetParent(element);
                }
            }
            catch (ElementNotAvailableException) { }
            catch (InvalidOperationException) { }
        }

        if (visibleProcessId <= 0 || visibleProcessId == _selfProcessId) return null;

        var candidates = CaptureCandidates(700, cancellationToken)
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

        return best is not null && bestScore >= 25 ? best.Bounds : null;
    }

    private IReadOnlyList<UiElementCandidate> CaptureCandidates(int maxCandidates, CancellationToken cancellationToken, int? rootProcessId = null)
    {
        var root = AutomationElement.RootElement;
        var walker = TreeWalker.ControlViewWalker;
        var queue = new Queue<(AutomationElement Element, int Depth)>();
        if (rootProcessId is > 0) EnqueueProcessSurfaceRoots(rootProcessId.Value, queue);
        else EnqueueChildren(walker, root, 0, queue);

        var interactivePoolLimit = Math.Max(900, maxCandidates * 3);
        var contextPoolLimit = Math.Max(180, maxCandidates / 2);
        var interactive = new List<UiElementCandidate>(interactivePoolLimit);
        var context = new List<UiElementCandidate>(contextPoolLimit);
        var processNames = new Dictionary<int, string>();
        var visited = 0;
        var stopwatch = Stopwatch.StartNew();

        while (queue.Count > 0 && visited < 7500 && stopwatch.ElapsedMilliseconds < 2700)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (element, depth) = queue.Dequeue();
            visited++;

            try
            {
                var current = element.Current;
                if (current.ProcessId != _selfProcessId && !current.IsOffscreen && current.IsEnabled)
                {
                    var rect = current.BoundingRectangle;
                    var typeName = current.ControlType?.ProgrammaticName ?? string.Empty;
                    var isPassword = current.IsPassword;
                    var isInput = typeName.EndsWith("Edit", StringComparison.Ordinal) || typeName.EndsWith("ComboBox", StringComparison.Ordinal);
                    var rawName = current.Name ?? string.Empty;
                    var name = isPassword ? "[password field]" : isInput ? "[input field]" : rawName;
                    var automationId = current.AutomationId ?? string.Empty;
                    var className = current.ClassName ?? string.Empty;
                    var processName = GetProcessName(current.ProcessId, processNames);
                    if (isInput && !isPassword)
                        automationId = AnnotateInputSemanticRole(automationId, rawName, className, processName);
                    var isInteractive = IsInteractiveType(typeName);
                    var isContext = !isInteractive && IsContextType(typeName, name, rect);

                    if (isInteractive && interactive.Count < interactivePoolLimit && ShouldKeep(name, automationId, className, rect))
                    {
                        var actionState = ReadActionState(element, typeName, isPassword);
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
                            $"c{context.Count + 1}", Trim(name, 180), Trim(automationId, 120), Trim(className, 120),
                            Trim(typeName.Replace("ControlType.", string.Empty), 80), processName,
                            false, current.IsEnabled, current.IsKeyboardFocusable, current.HasKeyboardFocus, isPassword,
                            rect.X, rect.Y, rect.Width, rect.Height, current.ProcessId));
                    }
                }
            }
            catch (ElementNotAvailableException) { }
            catch (InvalidOperationException) { }

            if (depth < 10) EnqueueChildren(walker, element, depth + 1, queue);
        }

        var contextBudget = Math.Min(context.Count, Math.Max(45, maxCandidates / 6));
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

    private static ActionState ReadActionState(AutomationElement element, string typeName, bool isPassword)
    {
        string? value = null;
        string? toggleState = null;
        bool? selected = null;
        string? expandCollapseState = null;

        try
        {
            if (!isPassword && (typeName.EndsWith("Edit", StringComparison.Ordinal) || typeName.EndsWith("ComboBox", StringComparison.Ordinal)) &&
                element.TryGetCurrentPattern(ValuePattern.Pattern, out var valuePattern) && valuePattern is ValuePattern valueValue)
            {
                // Keep only whether a value exists. The user-entered string itself is intentionally discarded
                // immediately and never stored in UiElementCandidate, history, telemetry or cloud payloads.
                value = string.IsNullOrEmpty(valueValue.Current.Value) ? null : "present";
            }
        }
        catch (ElementNotAvailableException) { }
        catch (InvalidOperationException) { }

        try
        {
            if ((typeName.EndsWith("CheckBox", StringComparison.Ordinal) || typeName.EndsWith("RadioButton", StringComparison.Ordinal)) &&
                element.TryGetCurrentPattern(TogglePattern.Pattern, out var togglePattern) && togglePattern is TogglePattern toggleValue)
                toggleState = toggleValue.Current.ToggleState.ToString();
        }
        catch (ElementNotAvailableException) { }
        catch (InvalidOperationException) { }

        try
        {
            if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selectionPattern) && selectionPattern is SelectionItemPattern selectionValue)
                selected = selectionValue.Current.IsSelected;
        }
        catch (ElementNotAvailableException) { }
        catch (InvalidOperationException) { }

        try
        {
            if (element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var expandPattern) && expandPattern is ExpandCollapsePattern expandValue)
                expandCollapseState = expandValue.Current.ExpandCollapseState.ToString();
        }
        catch (ElementNotAvailableException) { }
        catch (InvalidOperationException) { }

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
               typeName.EndsWith("TitleBar", StringComparison.Ordinal) || typeName.EndsWith("Document", StringComparison.Ordinal);
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

    private static void EnqueueProcessSurfaceRoots(int processId, Queue<(AutomationElement Element, int Depth)> queue)
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

    private static void EnqueueProcessRoots(int processId, Queue<(AutomationElement Element, int Depth)> queue)
    {
        try
        {
            var condition = new PropertyCondition(AutomationElement.ProcessIdProperty, processId);
            var roots = AutomationElement.RootElement.FindAll(TreeScope.Children, condition);
            foreach (AutomationElement root in roots) queue.Enqueue((root, 0));
        }
        catch (ElementNotAvailableException) { }
        catch (InvalidOperationException) { }
    }

    private static void EnqueueChildren(TreeWalker walker, AutomationElement parent, int depth, Queue<(AutomationElement Element, int Depth)> queue)
    {
        try
        {
            var child = walker.GetFirstChild(parent);
            while (child is not null) { queue.Enqueue((child, depth)); child = walker.GetNextSibling(child); }
        }
        catch (ElementNotAvailableException) { }
        catch (InvalidOperationException) { }
    }

    private readonly record struct ActionState(string? Value, string? ToggleState, bool? Selected, string? ExpandCollapseState);
}
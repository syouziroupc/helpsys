using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using HelpSys.Models;

namespace HelpSys.Services;

public sealed class UiAutomationScanner
{
    private readonly int _selfProcessId = Environment.ProcessId;

    public Task<IReadOnlyList<UiElementCandidate>> CaptureCandidatesAsync(int maxCandidates = 360, CancellationToken cancellationToken = default)
        => Task.Run(() => CaptureCandidates(maxCandidates, cancellationToken), cancellationToken);

    public Task<UiTarget?> FindBestTargetAsync(IReadOnlyList<string> hints, CancellationToken cancellationToken = default)
        => Task.Run(() => FindBestTarget(hints, cancellationToken), cancellationToken);

    private IReadOnlyList<UiElementCandidate> CaptureCandidates(int maxCandidates, CancellationToken cancellationToken)
    {
        var root = AutomationElement.RootElement;
        var walker = TreeWalker.ControlViewWalker;
        var queue = new Queue<(AutomationElement Element, int Depth)>();
        EnqueueChildren(walker, root, 0, queue);

        var output = new List<UiElementCandidate>(Math.Min(maxCandidates, 400));
        var processNames = new Dictionary<int, string>();
        var visited = 0;
        var stopwatch = Stopwatch.StartNew();

        while (queue.Count > 0 && output.Count < maxCandidates && visited < 6000 && stopwatch.ElapsedMilliseconds < 2200)
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
                    var name = isPassword ? "[password field]" : current.Name ?? string.Empty;
                    var automationId = current.AutomationId ?? string.Empty;
                    var className = current.ClassName ?? string.Empty;

                    if (ShouldInclude(typeName, name, automationId, className, rect))
                    {
                        output.Add(new UiElementCandidate(
                            $"u{output.Count + 1}", Trim(name, 180), Trim(automationId, 120), Trim(className, 120),
                            Trim(typeName.Replace("ControlType.", string.Empty), 80), GetProcessName(current.ProcessId, processNames),
                            current.IsEnabled, current.IsKeyboardFocusable, current.HasKeyboardFocus, isPassword,
                            rect.X, rect.Y, rect.Width, rect.Height, current.ProcessId));
                    }
                }
            }
            catch (ElementNotAvailableException) { }
            catch (InvalidOperationException) { }

            if (depth < 10) EnqueueChildren(walker, element, depth + 1, queue);
        }

        return output;
    }

    private UiTarget? FindBestTarget(IReadOnlyList<string> hints, CancellationToken cancellationToken)
    {
        if (hints.Count == 0) return null;
        var root = AutomationElement.RootElement;
        var walker = TreeWalker.ControlViewWalker;
        var queue = new Queue<(AutomationElement Element, int Depth)>();
        EnqueueChildren(walker, root, 0, queue);
        UiTarget? best = null;
        var visited = 0;
        var stopwatch = Stopwatch.StartNew();

        while (queue.Count > 0 && visited < 3500 && stopwatch.ElapsedMilliseconds < 1800)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (element, depth) = queue.Dequeue();
            visited++;
            try
            {
                var current = element.Current;
                if (current.ProcessId != _selfProcessId && !current.IsOffscreen)
                {
                    var rect = current.BoundingRectangle;
                    if (!rect.IsEmpty && rect.Width >= 8 && rect.Height >= 8)
                    {
                        var typeName = current.ControlType?.ProgrammaticName ?? string.Empty;
                        var score = Score(current.Name, current.AutomationId, current.ClassName, typeName, rect, hints);
                        if (score > 0 && (best is null || score > best.Score))
                            best = new UiTarget(current.Name ?? string.Empty, current.AutomationId ?? string.Empty, typeName, rect, current.ProcessId, score);
                    }
                }
            }
            catch (ElementNotAvailableException) { }
            catch (InvalidOperationException) { }
            if (depth < 9) EnqueueChildren(walker, element, depth + 1, queue);
        }
        return best;
    }

    private static bool ShouldInclude(string typeName, string name, string automationId, string className, Rect rect)
    {
        if (rect.IsEmpty || rect.Width < 8 || rect.Height < 8) return false;
        if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(automationId) && string.IsNullOrWhiteSpace(className)) return false;
        return typeName.EndsWith("Button", StringComparison.Ordinal) || typeName.EndsWith("ListItem", StringComparison.Ordinal) ||
               typeName.EndsWith("MenuItem", StringComparison.Ordinal) || typeName.EndsWith("Hyperlink", StringComparison.Ordinal) ||
               typeName.EndsWith("TabItem", StringComparison.Ordinal) || typeName.EndsWith("Edit", StringComparison.Ordinal) ||
               typeName.EndsWith("ComboBox", StringComparison.Ordinal) || typeName.EndsWith("CheckBox", StringComparison.Ordinal) ||
               typeName.EndsWith("RadioButton", StringComparison.Ordinal) || typeName.EndsWith("TreeItem", StringComparison.Ordinal) ||
               typeName.EndsWith("Window", StringComparison.Ordinal) || typeName.EndsWith("Pane", StringComparison.Ordinal);
    }

    private static string GetProcessName(int processId, Dictionary<int, string> cache)
    {
        if (processId <= 0) return string.Empty;
        if (cache.TryGetValue(processId, out var cached)) return cached;
        try { cached = Process.GetProcessById(processId).ProcessName; } catch { cached = string.Empty; }
        cache[processId] = cached;
        return cached;
    }

    private static string Trim(string value, int max) => value.Length <= max ? value : value[..max];

    private static void EnqueueChildren(TreeWalker walker, AutomationElement parent, int depth, Queue<(AutomationElement Element, int Depth)> queue)
    {
        try
        {
            var child = walker.GetFirstChild(parent);
            while (child is not null) { queue.Enqueue((child, depth)); child = walker.GetNextSibling(child); }
        }
        catch (ElementNotAvailableException) { }
    }

    private static double Score(string? name, string? automationId, string? className, string controlType, Rect rect, IReadOnlyList<string> hints)
    {
        var fields = new[] { name ?? string.Empty, automationId ?? string.Empty, className ?? string.Empty, controlType };
        double score = 0;
        for (var i = 0; i < hints.Count; i++)
        {
            var hint = hints[i].Trim();
            if (hint.Length == 0) continue;
            var weight = Math.Max(1, hints.Count - i);
            foreach (var field in fields)
            {
                if (field.Equals(hint, StringComparison.OrdinalIgnoreCase)) score += 100 * weight;
                else if (field.Contains(hint, StringComparison.OrdinalIgnoreCase)) score += 30 * weight;
            }
        }
        if (controlType.EndsWith("Button", StringComparison.Ordinal) || controlType.EndsWith("ListItem", StringComparison.Ordinal) || controlType.EndsWith("MenuItem", StringComparison.Ordinal) || controlType.EndsWith("Hyperlink", StringComparison.Ordinal) || controlType.EndsWith("TabItem", StringComparison.Ordinal)) score += 25;
        if (rect.Width > 1400 || rect.Height > 900) score -= 15;
        return score;
    }
}

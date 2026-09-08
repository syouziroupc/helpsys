using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using HelpSys.Models;

namespace HelpSys.Services;

public sealed class UiAutomationScanner
{
    private readonly int _selfProcessId = Environment.ProcessId;

    public Task<UiTarget?> FindBestTargetAsync(IReadOnlyList<string> hints, CancellationToken cancellationToken = default)
    {
        return Task.Run(() => FindBestTarget(hints, cancellationToken), cancellationToken);
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
                        {
                            best = new UiTarget(
                                current.Name ?? string.Empty,
                                current.AutomationId ?? string.Empty,
                                typeName,
                                rect,
                                current.ProcessId,
                                score);
                        }
                    }
                }
            }
            catch (ElementNotAvailableException)
            {
            }
            catch (InvalidOperationException)
            {
            }

            if (depth < 9)
            {
                EnqueueChildren(walker, element, depth + 1, queue);
            }
        }

        return best;
    }

    private static void EnqueueChildren(TreeWalker walker, AutomationElement parent, int depth, Queue<(AutomationElement Element, int Depth)> queue)
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
        catch (ElementNotAvailableException)
        {
        }
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

        if (controlType.EndsWith("Button", StringComparison.Ordinal) ||
            controlType.EndsWith("ListItem", StringComparison.Ordinal) ||
            controlType.EndsWith("MenuItem", StringComparison.Ordinal) ||
            controlType.EndsWith("Hyperlink", StringComparison.Ordinal) ||
            controlType.EndsWith("TabItem", StringComparison.Ordinal))
        {
            score += 25;
        }

        if (rect.Width > 1400 || rect.Height > 900) score -= 15;
        return score;
    }
}

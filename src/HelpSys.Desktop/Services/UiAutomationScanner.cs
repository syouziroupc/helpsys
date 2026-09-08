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
        var first = walker.GetFirstChild(root);
        if (first is not null) queue.Enqueue((first, 0));

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
                        var score = Score(current.Name, current.AutomationId, current.ClassName, current.ControlType.ProgrammaticName, hints);
                        if (score > 0 && (best is null || score > best.Score))
                        {
                            best = new UiTarget(
                                current.Name ?? string.Empty,
                                current.AutomationId ?? string.Empty,
                                current.ControlType?.ProgrammaticName ?? string.Empty,
                                rect,
                                current.ProcessId,
                                score);
                        }
                    }
                }
            }
            catch (ElementNotAvailableException)
            {
                // UI changed while it was being inspected. Skip the stale element.
            }
            catch (InvalidOperationException)
            {
                // Some providers disappear or reject property access. Continue scanning.
            }

            if (depth < 9)
            {
                try
                {
                    var child = walker.GetFirstChild(element);
                    while (child is not null)
                    {
                        queue.Enqueue((child, depth + 1));
                        child = walker.GetNextSibling(child);
                    }
                }
                catch (ElementNotAvailableException)
                {
                }
            }

            if (queue.Count == 0)
            {
                try
                {
                    var sibling = walker.GetNextSibling(element);
                    if (sibling is not null) queue.Enqueue((sibling, depth));
                }
                catch (ElementNotAvailableException)
                {
                }
            }
        }

        return best;
    }

    private static double Score(string? name, string? automationId, string? className, string? controlType, IReadOnlyList<string> hints)
    {
        var fields = new[] { name ?? string.Empty, automationId ?? string.Empty, className ?? string.Empty, controlType ?? string.Empty };
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

        return score;
    }
}

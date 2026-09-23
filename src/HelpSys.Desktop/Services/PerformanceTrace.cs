using System.Collections.Concurrent;
using System.Diagnostics;

namespace HelpSys.Services;

public sealed record PerformanceTraceEvent(
    long Sequence,
    DateTime TimestampUtc,
    string Phase,
    int ElapsedMs,
    bool Success);

/// <summary>
/// Content-free, bounded, in-memory performance trace used to identify freezes/latency by phase.
/// It never stores user text, URLs, UI labels, screenshots, input values, or files.
/// Diagnostic mode additionally mirrors the same metadata to System.Diagnostics.Trace.
/// </summary>
public static class PerformanceTrace
{
    private const int MaxEvents = 256;
    private static readonly ConcurrentQueue<PerformanceTraceEvent> Events = new();
    private static long _sequence;
    private static readonly bool DiagnosticEnabled = string.Equals(
        Environment.GetEnvironmentVariable("HELPSYS_DIAGNOSTIC_MODE"),
        "1",
        StringComparison.Ordinal);

    public static async Task<T> MeasureAsync<T>(string phase, Func<Task<T>> action)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = await action().ConfigureAwait(false);
            Record(phase, stopwatch.Elapsed, success: true);
            return result;
        }
        catch
        {
            Record(phase, stopwatch.Elapsed, success: false);
            throw;
        }
    }

    public static async Task MeasureAsync(string phase, Func<Task> action)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await action().ConfigureAwait(false);
            Record(phase, stopwatch.Elapsed, success: true);
        }
        catch
        {
            Record(phase, stopwatch.Elapsed, success: false);
            throw;
        }
    }

    public static T Measure<T>(string phase, Func<T> action)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = action();
            Record(phase, stopwatch.Elapsed, success: true);
            return result;
        }
        catch
        {
            Record(phase, stopwatch.Elapsed, success: false);
            throw;
        }
    }

    public static IReadOnlyList<PerformanceTraceEvent> Snapshot()
        => Events.ToArray();

    private static void Record(string phase, TimeSpan elapsed, bool success)
    {
        var safePhase = NormalizePhase(phase);
        var item = new PerformanceTraceEvent(
            Interlocked.Increment(ref _sequence),
            DateTime.UtcNow,
            safePhase,
            (int)Math.Clamp(elapsed.TotalMilliseconds, 0, int.MaxValue),
            success);

        Events.Enqueue(item);
        while (Events.Count > MaxEvents && Events.TryDequeue(out _)) { }

        if (DiagnosticEnabled)
            Trace.WriteLine($"[perf] seq={item.Sequence} phase={item.Phase} ms={item.ElapsedMs} ok={item.Success}");
    }

    private static string NormalizePhase(string phase)
    {
        if (string.IsNullOrWhiteSpace(phase)) return "unknown";
        var sanitized = new string(phase
            .Where(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-' or '_')
            .Take(64)
            .ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "unknown" : sanitized;
    }
}

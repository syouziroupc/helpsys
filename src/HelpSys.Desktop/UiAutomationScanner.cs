using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using HelpSys.Models;

namespace HelpSys;

// MainWindow-facing UIA facade. The lower-level scanner remains the compatibility fallback,
// while normal observation is narrowed to the actual foreground HWND whenever Windows can
// bind that HWND to the requested process. This prevents unrelated windows from the same
// process from contaminating candidate selection without weakening shell compatibility.
public sealed class UiAutomationScanner
{
    private static readonly string[] ShellSurfaceProcesses = ["explorer", "SearchHost", "StartMenuExperienceHost"];
    private readonly HelpSys.Services.UiAutomationScanner _inner = new();

    public Task<IReadOnlyList<UiElementCandidate>> CaptureCandidatesAsync(
        int maxCandidates = 360,
        CancellationToken cancellationToken = default)
        => _inner.CaptureCandidatesAsync(maxCandidates, cancellationToken);

    public async Task<IReadOnlyList<UiElementCandidate>> CaptureCandidatesForProcessAsync(
        int processId,
        int maxCandidates = 360,
        CancellationToken cancellationToken = default)
    {
        var candidates = await _inner.CaptureCandidatesForProcessAsync(processId, maxCandidates, cancellationToken);
        return ScopeToForegroundWindow(processId, candidates);
    }

    public Task<string> CaptureWindowDiagnosticsAsync(
        nint windowHandle,
        int expectedProcessId,
        CancellationToken cancellationToken = default)
        => _inner.CaptureWindowDiagnosticsAsync(windowHandle, expectedProcessId, cancellationToken);

    public async Task<UiElementCandidate?> RevalidateCandidateAsync(
        UiElementCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        var fresh = await _inner.RevalidateCandidateAsync(candidate, cancellationToken);
        return fresh is not null && IsAllowedByForegroundWindow(fresh, candidate.ProcessId) ? fresh : null;
    }

    public async Task<UiElementCandidate?> RevalidateCandidateAsync(
        UiElementCandidate candidate,
        int rootProcessId,
        CancellationToken cancellationToken = default)
    {
        var fresh = await _inner.RevalidateCandidateAsync(candidate, rootProcessId, cancellationToken);
        return fresh is not null && IsAllowedByForegroundWindow(fresh, rootProcessId) ? fresh : null;
    }

    public Task<Rect?> SnapToAccessibleBoundsAsync(
        Rect approximateBounds,
        CancellationToken cancellationToken = default)
        => _inner.SnapToAccessibleBoundsAsync(approximateBounds, cancellationToken);

    private static IReadOnlyList<UiElementCandidate> ScopeToForegroundWindow(
        int processId,
        IReadOnlyList<UiElementCandidate> candidates)
    {
        if (candidates.Count == 0 || processId <= 0 || IsShellSurfaceProcess(processId)) return candidates;
        if (!TryGetForegroundBounds(processId, out var foregroundBounds)) return candidates;

        var scoped = candidates
            .Where(candidate => candidate.ProcessId <= 0 || candidate.ProcessId == processId)
            .Where(candidate => IsInsideOrMostlyOverlapping(candidate.Bounds, foregroundBounds))
            .ToArray();

        // A broken or unusually sparse UIA tree must not become a new failure mode. If the
        // foreground HWND produced no actionable element, retain the established process scan.
        return scoped.Any(candidate => candidate.Interactable) ? scoped : candidates;
    }

    private static bool IsAllowedByForegroundWindow(UiElementCandidate candidate, int expectedProcessId)
    {
        if (expectedProcessId <= 0 || IsShellSurfaceProcess(expectedProcessId)) return true;
        if (!TryGetForegroundBounds(expectedProcessId, out var foregroundBounds)) return true;
        if (candidate.ProcessId > 0 && candidate.ProcessId != expectedProcessId) return false;
        return IsInsideOrMostlyOverlapping(candidate.Bounds, foregroundBounds);
    }

    private static bool TryGetForegroundBounds(int expectedProcessId, out Rect bounds)
    {
        bounds = Rect.Empty;
        var hwnd = GetForegroundWindow();
        if (hwnd == nint.Zero) return false;

        _ = GetWindowThreadProcessId(hwnd, out var actualProcessId);
        if (actualProcessId == 0 || actualProcessId != expectedProcessId) return false;
        if (!GetWindowRect(hwnd, out var rect)) return false;

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width < 16 || height < 16) return false;
        bounds = new Rect(rect.Left, rect.Top, width, height);
        return true;
    }

    private static bool IsInsideOrMostlyOverlapping(Rect candidate, Rect window)
    {
        if (candidate.IsEmpty || window.IsEmpty) return false;

        var center = new Point(candidate.Left + candidate.Width / 2d, candidate.Top + candidate.Height / 2d);
        if (window.Contains(center)) return true;
        if (!candidate.IntersectsWith(window)) return false;

        var intersection = Rect.Intersect(candidate, window);
        if (intersection.IsEmpty) return false;
        var candidateArea = Math.Max(1d, candidate.Width * candidate.Height);
        var intersectionArea = intersection.Width * intersection.Height;
        return intersectionArea / candidateArea >= 0.60d;
    }

    private static bool IsShellSurfaceProcess(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return ShellSurfaceProcesses.Contains(process.ProcessName, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out int processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hWnd, out NativeRect rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}

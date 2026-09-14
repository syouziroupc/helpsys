using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using HelpSys.Models;

namespace HelpSys;

// MainWindow-facing UIA facade. Normal applications are scoped to the actual foreground HWND.
// Windows shell surfaces are process-isolated instead of merging Explorer/Search/Start candidates
// into one mixed-PID set. Input evidence is restored locally for non-secret fields so search,
// address and ordinary text controls remain understandable without exposing password values.
public sealed class UiAutomationScanner
{
    private static readonly string[] ShellSurfaceProcesses = ["explorer", "SearchHost", "StartMenuExperienceHost", "ShellExperienceHost"];
    private readonly HelpSys.Services.UiAutomationScanner _inner = new();

    public async Task<IReadOnlyList<UiElementCandidate>> CaptureCandidatesAsync(
        int maxCandidates = 360,
        CancellationToken cancellationToken = default)
    {
        var candidates = await _inner.CaptureCandidatesAsync(maxCandidates, cancellationToken);
        return RestoreLocalInputEvidence(candidates, cancellationToken);
    }

    public async Task<IReadOnlyList<UiElementCandidate>> CaptureCandidatesForProcessAsync(
        int processId,
        int maxCandidates = 360,
        CancellationToken cancellationToken = default)
    {
        var candidates = await _inner.CaptureCandidatesForProcessAsync(processId, maxCandidates, cancellationToken);
        var scoped = ScopeToForegroundWindow(processId, candidates);
        return RestoreLocalInputEvidence(scoped, cancellationToken);
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
        if (fresh is null || !IsAllowedByForegroundWindow(fresh, candidate.ProcessId)) return null;
        return RestoreLocalInputEvidence([fresh], cancellationToken).FirstOrDefault();
    }

    public async Task<UiElementCandidate?> RevalidateCandidateAsync(
        UiElementCandidate candidate,
        int rootProcessId,
        CancellationToken cancellationToken = default)
    {
        var fresh = await _inner.RevalidateCandidateAsync(candidate, rootProcessId, cancellationToken);
        if (fresh is null || !IsAllowedByForegroundWindow(fresh, rootProcessId)) return null;
        return RestoreLocalInputEvidence([fresh], cancellationToken).FirstOrDefault();
    }

    public Task<Rect?> SnapToAccessibleBoundsAsync(
        Rect approximateBounds,
        CancellationToken cancellationToken = default)
        => _inner.SnapToAccessibleBoundsAsync(approximateBounds, cancellationToken);

    private static IReadOnlyList<UiElementCandidate> ScopeToForegroundWindow(
        int processId,
        IReadOnlyList<UiElementCandidate> candidates)
    {
        if (candidates.Count == 0 || processId <= 0) return candidates;

        if (IsShellSurfaceProcess(processId))
        {
            // The lower scanner intentionally knows about related shell processes, but mixing their
            // PIDs into one guidance snapshot conflicts with the exact-HWND screenshot boundary.
            // Use only the process Windows says is currently the shell foreground. If that process
            // exposes no UIA controls, return an empty structured set and let verified full-monitor
            // vision handle the shell instead of borrowing controls from a different shell process.
            return candidates
                .Where(candidate => candidate.ProcessId <= 0 || candidate.ProcessId == processId)
                .ToArray();
        }

        if (!TryGetForegroundBounds(processId, out var foregroundBounds)) return candidates;

        var scoped = candidates
            .Where(candidate => candidate.ProcessId <= 0 || candidate.ProcessId == processId)
            .Where(candidate => IsInsideOrMostlyOverlapping(candidate.Bounds, foregroundBounds))
            .ToArray();

        // Sparse third-party UIA trees remain usable through the process scan, but only for the same
        // process. This fallback never widens to another process or an unrelated window owner.
        return scoped.Any(candidate => candidate.Interactable) ? scoped : candidates;
    }

    private static IReadOnlyList<UiElementCandidate> RestoreLocalInputEvidence(
        IReadOnlyList<UiElementCandidate> candidates,
        CancellationToken cancellationToken)
    {
        if (candidates.Count == 0) return candidates;
        var restored = new UiElementCandidate[candidates.Count];
        for (var index = 0; index < candidates.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            restored[index] = RestoreLocalInputEvidence(candidates[index]);
        }
        return restored;
    }

    private static UiElementCandidate RestoreLocalInputEvidence(UiElementCandidate candidate)
    {
        if (candidate.Password || candidate.Bounds.IsEmpty ||
            candidate.ControlType is not ("Edit" or "ComboBox"))
            return candidate;

        try
        {
            var point = new Point(candidate.X + candidate.Width / 2d, candidate.Y + candidate.Height / 2d);
            var element = AutomationElement.FromPoint(point);
            var walker = TreeWalker.ControlViewWalker;

            for (var depth = 0; element is not null && depth < 7; depth++)
            {
                var current = element.Current;
                var type = (current.ControlType?.ProgrammaticName ?? string.Empty)
                    .Replace("ControlType.", string.Empty, StringComparison.Ordinal);
                var sameProcess = candidate.ProcessId <= 0 || current.ProcessId == candidate.ProcessId;
                if (sameProcess && type is "Edit" or "ComboBox" && !current.IsPassword)
                {
                    var name = current.Name?.Trim() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(name)) name = candidate.Name;

                    string? value = null;
                    if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) && pattern is ValuePattern valuePattern)
                    {
                        value = valuePattern.Current.Value?.Trim();
                        if (value?.Length > 320) value = value[..320];
                        if (string.IsNullOrWhiteSpace(value)) value = null;
                    }

                    return candidate with
                    {
                        Name = string.IsNullOrWhiteSpace(name) ? "[input field]" : name,
                        Value = value
                    };
                }

                element = walker.GetParent(element);
            }
        }
        catch (ElementNotAvailableException) { }
        catch (InvalidOperationException) { }
        catch (COMException) { }

        return candidate;
    }

    private static bool IsAllowedByForegroundWindow(UiElementCandidate candidate, int expectedProcessId)
    {
        if (expectedProcessId <= 0) return true;
        if (IsShellSurfaceProcess(expectedProcessId))
            return candidate.ProcessId <= 0 || candidate.ProcessId == expectedProcessId;
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

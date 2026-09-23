using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using HelpSys.Models;

namespace HelpSys.Services;

internal sealed class UiAutomationObserverClient : IDisposable
{
    private static readonly TimeSpan RequestTimeout = OutlawModePolicy.Enabled
        ? TimeSpan.FromSeconds(22)
        : TimeSpan.FromMilliseconds(3800);
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

    private Process? _process;
    private StreamWriter? _input;
    private StreamReader? _output;
    private bool _disposed;

    public async Task<IReadOnlyList<UiElementCandidate>> CaptureCandidatesAsync(
        int maxCandidates,
        CancellationToken cancellationToken)
    {
        var response = await PerformanceTrace.MeasureAsync(
            "uia.capture",
            () => SendAsync(
                new UiAutomationObserverRequest(NewId(), "capture", MaxCandidates: maxCandidates),
                cancellationToken));
        return response.Candidates ?? [];
    }

    public async Task<IReadOnlyList<UiElementCandidate>> CaptureCandidatesForProcessAsync(
        int processId,
        int maxCandidates,
        CancellationToken cancellationToken)
    {
        if (processId <= 0) return [];
        var response = await PerformanceTrace.MeasureAsync(
            "uia.capture-process",
            () => SendAsync(
                new UiAutomationObserverRequest(
                    NewId(),
                    "capture-process",
                    ProcessId: processId,
                    MaxCandidates: maxCandidates),
                cancellationToken));
        return response.Candidates ?? [];
    }

    public async Task<UiElementCandidate?> RevalidateCandidateAsync(
        UiElementCandidate candidate,
        int? rootProcessId,
        CancellationToken cancellationToken)
    {
        var response = await PerformanceTrace.MeasureAsync(
            "uia.revalidate",
            () => SendAsync(
                new UiAutomationObserverRequest(
                    NewId(),
                    "revalidate",
                    Candidate: candidate,
                    RootProcessId: rootProcessId ?? 0),
                cancellationToken));
        return response.Candidate;
    }

    public async Task<Rect?> SnapToAccessibleBoundsAsync(
        Rect approximateBounds,
        CancellationToken cancellationToken)
    {
        var response = await PerformanceTrace.MeasureAsync(
            "uia.snap",
            () => SendAsync(
                new UiAutomationObserverRequest(
                    NewId(),
                    "snap",
                    X: approximateBounds.X,
                    Y: approximateBounds.Y,
                    Width: approximateBounds.Width,
                    Height: approximateBounds.Height),
                cancellationToken));
        return response.Bounds is { } bounds
            ? new Rect(bounds.X, bounds.Y, bounds.Width, bounds.Height)
            : null;
    }

    public async Task<UiElementCandidate?> SnapToAccessibleCandidateAsync(
        Rect approximateBounds,
        CancellationToken cancellationToken)
    {
        var response = await PerformanceTrace.MeasureAsync(
            "uia.snap-candidate",
            () => SendAsync(
                new UiAutomationObserverRequest(
                    NewId(),
                    "snap-candidate",
                    X: approximateBounds.X,
                    Y: approximateBounds.Y,
                    Width: approximateBounds.Width,
                    Height: approximateBounds.Height),
                cancellationToken));
        return response.Candidate;
    }

    public async Task<string> CaptureWindowDiagnosticsAsync(
        nint windowHandle,
        int expectedProcessId,
        CancellationToken cancellationToken)
    {
        var response = await PerformanceTrace.MeasureAsync(
            "uia.diagnostics",
            () => SendAsync(
                new UiAutomationObserverRequest(
                    NewId(),
                    "diagnostics",
                    WindowHandle: (long)windowHandle,
                    ExpectedProcessId: expectedProcessId),
                cancellationToken));
        return response.Diagnostics ?? "observer=missing-diagnostics";
    }

    private async Task<UiAutomationObserverResponse> SendAsync(
        UiAutomationObserverRequest request,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _requestGate.WaitAsync(cancellationToken);
        try
        {
            EnsureStarted();
            var json = JsonSerializer.Serialize(request, _jsonOptions);
            await _input!.WriteLineAsync(json.AsMemory(), cancellationToken);
            await _input.FlushAsync(cancellationToken);

            string? line;
            try
            {
                line = await _output!.ReadLineAsync(cancellationToken)
                    .AsTask()
                    .WaitAsync(RequestTimeout, cancellationToken);
            }
            catch (TimeoutException)
            {
                RestartObserver();
                throw new TimeoutException("UI Automation observer exceeded its hard deadline and was restarted.");
            }
            catch (OperationCanceledException)
            {
                RestartObserver();
                throw;
            }

            if (line is null)
            {
                RestartObserver();
                throw new InvalidOperationException("UI Automation observer exited unexpectedly.");
            }

            var response = JsonSerializer.Deserialize<UiAutomationObserverResponse>(line, _jsonOptions)
                           ?? throw new InvalidOperationException("UI Automation observer returned an empty response.");
            if (!string.Equals(response.Id, request.Id, StringComparison.Ordinal))
            {
                RestartObserver();
                throw new InvalidOperationException("UI Automation observer response generation did not match the request.");
            }
            if (!response.Ok)
                throw new InvalidOperationException($"UI Automation observer rejected the request: {response.Error}");

            return response;
        }
        catch (IOException)
        {
            RestartObserver();
            throw;
        }
        finally
        {
            _requestGate.Release();
        }
    }

    private void EnsureStarted()
    {
        if (_process is { HasExited: false } && _input is not null && _output is not null) return;
        StopObserver();

        var processPath = Environment.ProcessPath
                          ?? throw new InvalidOperationException("Cannot resolve the HelpSys executable path.");
        var commandLineArgs = Environment.GetCommandLineArgs();
        ProcessStartInfo startInfo;

        if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var assemblyPath = commandLineArgs.FirstOrDefault()
                               ?? throw new InvalidOperationException("Cannot resolve the HelpSys assembly path.");
            startInfo = new ProcessStartInfo(
                processPath,
                $"\"{assemblyPath}\" --uia-observer --parent-pid {Environment.ProcessId}");
        }
        else
        {
            startInfo = new ProcessStartInfo(
                processPath,
                $"--uia-observer --parent-pid {Environment.ProcessId}");
        }

        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        startInfo.RedirectStandardInput = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = false;

        _process = Process.Start(startInfo)
                   ?? throw new InvalidOperationException("Failed to start the UI Automation observer.");
        _input = _process.StandardInput;
        _output = _process.StandardOutput;
    }

    private void RestartObserver() => StopObserver();

    private void StopObserver()
    {
        try { _input?.Dispose(); } catch { }
        try { _output?.Dispose(); } catch { }
        _input = null;
        _output = null;

        if (_process is not null)
        {
            try
            {
                if (!_process.HasExited) _process.Kill(entireProcessTree: true);
            }
            catch { }
            try { _process.Dispose(); } catch { }
            _process = null;
        }
    }

    private static string NewId() => Guid.NewGuid().ToString("N");

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopObserver();
        _requestGate.Dispose();
    }
}

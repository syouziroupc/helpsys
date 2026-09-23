using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Windows;

namespace HelpSys.Services;

internal static class UiAutomationObserverHost
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static bool IsObserverProcess =>
        Environment.GetCommandLineArgs().Any(x => x.Equals("--uia-observer", StringComparison.OrdinalIgnoreCase));

    public static async Task<int> RunAsync()
    {
        var parentProcessId = ReadParentProcessId();
        if (parentProcessId > 0) _ = Task.Run(() => MonitorParentAsync(parentProcessId));

        using var executor = new MtaAutomationExecutor();
        string? line;
        while ((line = await Console.In.ReadLineAsync()) is not null)
        {
            UiAutomationObserverResponse response;
            try
            {
                var request = JsonSerializer.Deserialize<UiAutomationObserverRequest>(line, JsonOptions)
                              ?? throw new InvalidOperationException("Observer request was empty.");
                response = await executor.ExecuteAsync(request).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                response = new UiAutomationObserverResponse(
                    "unknown",
                    false,
                    $"{ex.GetType().Name}: {ex.Message}");
            }

            await Console.Out.WriteLineAsync(JsonSerializer.Serialize(response, JsonOptions));
            await Console.Out.FlushAsync();
        }

        return 0;
    }

    private static UiAutomationObserverResponse ExecuteOnAutomationThread(
        UiAutomationScanner scanner,
        UiAutomationObserverRequest request)
    {
        try
        {
            return request.Operation switch
            {
                "capture" => new UiAutomationObserverResponse(
                    request.Id,
                    true,
                    Candidates: scanner.CaptureCandidatesAsync(
                        request.MaxCandidates <= 0 ? 360 : request.MaxCandidates).GetAwaiter().GetResult()),

                "capture-process" => new UiAutomationObserverResponse(
                    request.Id,
                    true,
                    Candidates: scanner.CaptureCandidatesForProcessAsync(
                        request.ProcessId,
                        request.MaxCandidates <= 0 ? 360 : request.MaxCandidates).GetAwaiter().GetResult()),

                "revalidate" when request.Candidate is not null => new UiAutomationObserverResponse(
                    request.Id,
                    true,
                    Candidate: request.RootProcessId > 0
                        ? scanner.RevalidateCandidateAsync(request.Candidate, request.RootProcessId).GetAwaiter().GetResult()
                        : scanner.RevalidateCandidateAsync(request.Candidate).GetAwaiter().GetResult()),

                "snap" => BuildBoundsResponse(
                    request.Id,
                    scanner.SnapToAccessibleBoundsAsync(
                        new Rect(request.X, request.Y, request.Width, request.Height)).GetAwaiter().GetResult()),

                "snap-candidate" => new UiAutomationObserverResponse(
                    request.Id,
                    true,
                    Candidate: scanner.SnapToAccessibleCandidateAsync(
                        new Rect(request.X, request.Y, request.Width, request.Height)).GetAwaiter().GetResult()),

                "diagnostics" => new UiAutomationObserverResponse(
                    request.Id,
                    true,
                    Diagnostics: scanner.CaptureWindowDiagnosticsAsync(
                        (nint)request.WindowHandle,
                        request.ExpectedProcessId).GetAwaiter().GetResult()),

                _ => new UiAutomationObserverResponse(request.Id, false, "unknown_operation")
            };
        }
        catch (Exception ex)
        {
            return new UiAutomationObserverResponse(
                request.Id,
                false,
                $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private sealed class MtaAutomationExecutor : IDisposable
    {
        private readonly BlockingCollection<WorkItem> _queue = new();
        private readonly Thread _thread;
        private bool _disposed;

        public MtaAutomationExecutor()
        {
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "HelpSys.UIAutomation.MTA"
            };
            _thread.SetApartmentState(ApartmentState.MTA);
            _thread.Start();
        }

        public Task<UiAutomationObserverResponse> ExecuteAsync(UiAutomationObserverRequest request)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var completion = new TaskCompletionSource<UiAutomationObserverResponse>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _queue.Add(new WorkItem(request, completion));
            return completion.Task;
        }

        private void Run()
        {
            var scanner = new UiAutomationScanner(forceLocal: true);
            foreach (var item in _queue.GetConsumingEnumerable())
            {
                try
                {
                    item.Completion.TrySetResult(ExecuteOnAutomationThread(scanner, item.Request));
                }
                catch (Exception ex)
                {
                    item.Completion.TrySetResult(new UiAutomationObserverResponse(
                        item.Request.Id,
                        false,
                        $"{ex.GetType().Name}: {ex.Message}"));
                }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _queue.CompleteAdding();
            try { _thread.Join(TimeSpan.FromSeconds(1)); } catch { }
            _queue.Dispose();
        }

        private sealed record WorkItem(
            UiAutomationObserverRequest Request,
            TaskCompletionSource<UiAutomationObserverResponse> Completion);
    }

    private static UiAutomationObserverResponse BuildBoundsResponse(string id, Rect? bounds) =>
        bounds is { } value
            ? new UiAutomationObserverResponse(
                id,
                true,
                Bounds: new UiAutomationObserverBounds(value.X, value.Y, value.Width, value.Height))
            : new UiAutomationObserverResponse(id, true);

    private static int ReadParentProcessId()
    {
        var args = Environment.GetCommandLineArgs();
        for (var i = 0; i + 1 < args.Length; i++)
        {
            if (!args[i].Equals("--parent-pid", StringComparison.OrdinalIgnoreCase)) continue;
            if (int.TryParse(args[i + 1], out var processId)) return processId;
        }
        return 0;
    }

    private static async Task MonitorParentAsync(int parentProcessId)
    {
        while (true)
        {
            await Task.Delay(1500);
            try
            {
                using var parent = Process.GetProcessById(parentProcessId);
                if (!parent.HasExited) continue;
            }
            catch
            {
                Environment.Exit(0);
            }

            Environment.Exit(0);
        }
    }
}

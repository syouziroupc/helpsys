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

        var scanner = new UiAutomationScanner(forceLocal: true);
        string? line;
        while ((line = await Console.In.ReadLineAsync()) is not null)
        {
            UiAutomationObserverResponse response;
            try
            {
                var request = JsonSerializer.Deserialize<UiAutomationObserverRequest>(line, JsonOptions)
                              ?? throw new InvalidOperationException("Observer request was empty.");
                response = await ExecuteAsync(scanner, request);
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

    private static async Task<UiAutomationObserverResponse> ExecuteAsync(
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
                    Candidates: await scanner.CaptureCandidatesAsync(
                        request.MaxCandidates <= 0 ? 360 : request.MaxCandidates)),

                "capture-process" => new UiAutomationObserverResponse(
                    request.Id,
                    true,
                    Candidates: await scanner.CaptureCandidatesForProcessAsync(
                        request.ProcessId,
                        request.MaxCandidates <= 0 ? 360 : request.MaxCandidates)),

                "revalidate" when request.Candidate is not null => new UiAutomationObserverResponse(
                    request.Id,
                    true,
                    Candidate: request.RootProcessId > 0
                        ? await scanner.RevalidateCandidateAsync(request.Candidate, request.RootProcessId)
                        : await scanner.RevalidateCandidateAsync(request.Candidate)),

                "snap" => BuildBoundsResponse(
                    request.Id,
                    await scanner.SnapToAccessibleBoundsAsync(
                        new Rect(request.X, request.Y, request.Width, request.Height))),

                "snap-candidate" => new UiAutomationObserverResponse(
                    request.Id,
                    true,
                    Candidate: await scanner.SnapToAccessibleCandidateAsync(
                        new Rect(request.X, request.Y, request.Width, request.Height))),

                "diagnostics" => new UiAutomationObserverResponse(
                    request.Id,
                    true,
                    Diagnostics: await scanner.CaptureWindowDiagnosticsAsync(
                        (nint)request.WindowHandle,
                        request.ExpectedProcessId)),

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

using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using HelpSys.Models;

namespace HelpSys.Services;

public sealed class CloudGuideService : IDisposable
{
    private const string DefaultApiBase = "https://helpsys.syouziroupc.workers.dev";
    private readonly HttpClient _http;
    private readonly string _apiBase;
    private readonly string? _apiKey;
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

    public CloudGuideService()
    {
        _apiBase = (Environment.GetEnvironmentVariable("HELPSYS_API_BASE") ?? DefaultApiBase).TrimEnd('/');
        _apiKey = Environment.GetEnvironmentVariable("HELPSYS_API_KEY");

        // The guidance session owns the deadline. An independent HttpClient timeout would turn
        // slow-but-healthy inference into a TaskCanceledException reported as a network failure.
        _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<GuideDecision> PlanAsync(string request, IReadOnlyList<UiElementCandidate> elements, IReadOnlyList<GuideHistoryItem> history, SystemContextSnapshot systemContext, CancellationToken cancellationToken = default)
    {
        var body = new
        {
            request,
            history,
            systemContext,
            elements = elements.Select(x => new
            {
                id = x.Id,
                name = x.Name,
                automationId = x.AutomationId,
                className = x.ClassName,
                controlType = x.ControlType,
                processName = x.ProcessName,
                interactable = x.Interactable,
                enabled = x.Enabled,
                keyboardFocusable = x.KeyboardFocusable,
                focused = x.Focused,
                password = x.Password,
                x = x.X,
                y = x.Y,
                width = x.Width,
                height = x.Height
            })
        };

        try
        {
            return await SendAsync<GuideDecision>(() => CreateMessage(HttpMethod.Post, $"{_apiBase}/v1/guide", body), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // HttpClient often surfaces cancellation as TaskCanceledException. Normalize it to
            // plain OperationCanceledException so the UI's legacy network-error filter cannot
            // misclassify a deliberate stale-plan cancellation as a failed Internet connection.
            throw new OperationCanceledException(cancellationToken);
        }
    }

    public async Task<VisionGuideDecision> PlanVisionAsync(string request, ScreenCaptureFrame frame, IReadOnlyList<GuideHistoryItem> history, SystemContextSnapshot systemContext, CancellationToken cancellationToken = default)
    {
        var body = new
        {
            request,
            history,
            systemContext,
            image = frame.ImageDataUri,
            imageWidth = frame.ImageWidth,
            imageHeight = frame.ImageHeight
        };

        try
        {
            return await SendAsync<VisionGuideDecision>(() => CreateMessage(HttpMethod.Post, $"{_apiBase}/v1/vision-guide", body), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private HttpRequestMessage CreateMessage(HttpMethod method, string url, object body)
    {
        var message = new HttpRequestMessage(method, url) { Content = JsonContent.Create(body) };
        if (!string.IsNullOrWhiteSpace(_apiKey)) message.Headers.TryAddWithoutValidation("x-helpsys-key", _apiKey);
        return message;
    }

    private async Task<T> SendAsync<T>(Func<HttpRequestMessage> createMessage, CancellationToken cancellationToken)
    {
        Exception? lastTransientError = null;

        for (var attempt = 0; attempt < 2; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using var message = createMessage();
                using var response = await _http.SendAsync(message, cancellationToken);
                var body = await response.Content.ReadAsStringAsync(cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    return JsonSerializer.Deserialize<T>(body, _jsonOptions)
                           ?? throw new InvalidOperationException("HelpSys APIの応答を解析できませんでした。");
                }

                var apiError = new InvalidOperationException($"HelpSys API {((int)response.StatusCode)}: {Short(body)}");
                if (!IsTransientStatus((int)response.StatusCode) || attempt > 0) throw apiError;
                lastTransientError = apiError;
            }
            catch (HttpRequestException ex) when (attempt == 0 && !cancellationToken.IsCancellationRequested)
            {
                lastTransientError = ex;
            }

            await Task.Delay(350, cancellationToken);
        }

        throw lastTransientError ?? new HttpRequestException("HelpSys APIへの一時的な通信に失敗しました。");
    }

    private static bool IsTransientStatus(int statusCode) => statusCode is 408 or 429 or 500 or 502 or 503 or 504;
    private static string Short(string value) => value.Length <= 180 ? value : value[..180];
    public void Dispose() => _http.Dispose();
}

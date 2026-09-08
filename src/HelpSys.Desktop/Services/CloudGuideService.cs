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
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(18) };
    }

    public async Task<GuideDecision> PlanAsync(string request, IReadOnlyList<UiElementCandidate> elements, IReadOnlyList<GuideHistoryItem> history, CancellationToken cancellationToken = default)
    {
        using var message = CreateMessage(HttpMethod.Post, $"{_apiBase}/v1/guide", new
        {
            request,
            history,
            elements = elements.Select(x => new
            {
                id = x.Id,
                name = x.Name,
                automationId = x.AutomationId,
                className = x.ClassName,
                controlType = x.ControlType,
                processName = x.ProcessName,
                enabled = x.Enabled,
                keyboardFocusable = x.KeyboardFocusable,
                focused = x.Focused,
                password = x.Password
            })
        });

        return await SendAsync<GuideDecision>(message, cancellationToken);
    }

    public async Task<VisionGuideDecision> PlanVisionAsync(string request, ScreenCaptureFrame frame, IReadOnlyList<GuideHistoryItem> history, CancellationToken cancellationToken = default)
    {
        using var message = CreateMessage(HttpMethod.Post, $"{_apiBase}/v1/vision-guide", new
        {
            request,
            history,
            image = frame.ImageDataUri,
            imageWidth = frame.ImageWidth,
            imageHeight = frame.ImageHeight
        });

        return await SendAsync<VisionGuideDecision>(message, cancellationToken);
    }

    private HttpRequestMessage CreateMessage(HttpMethod method, string url, object body)
    {
        var message = new HttpRequestMessage(method, url) { Content = JsonContent.Create(body) };
        if (!string.IsNullOrWhiteSpace(_apiKey)) message.Headers.TryAddWithoutValidation("x-helpsys-key", _apiKey);
        return message;
    }

    private async Task<T> SendAsync<T>(HttpRequestMessage message, CancellationToken cancellationToken)
    {
        using var response = await _http.SendAsync(message, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"HelpSys API {((int)response.StatusCode)}: {Short(body)}");
        return JsonSerializer.Deserialize<T>(body, _jsonOptions) ?? throw new InvalidOperationException("HelpSys APIの応答を解析できませんでした。");
    }

    private static string Short(string value) => value.Length <= 180 ? value : value[..180];
    public void Dispose() => _http.Dispose();
}

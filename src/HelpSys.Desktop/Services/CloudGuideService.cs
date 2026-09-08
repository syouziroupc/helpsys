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

    public CloudGuideService()
    {
        _apiBase = (Environment.GetEnvironmentVariable("HELPSYS_API_BASE") ?? DefaultApiBase).TrimEnd('/');
        _apiKey = Environment.GetEnvironmentVariable("HELPSYS_API_KEY");
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
    }

    public async Task<GuideDecision> PlanAsync(
        string request,
        IReadOnlyList<UiElementCandidate> elements,
        CancellationToken cancellationToken = default)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, $"{_apiBase}/v1/guide")
        {
            Content = JsonContent.Create(new
            {
                request,
                elements = elements.Select(x => new
                {
                    id = x.Id,
                    name = x.Name,
                    automationId = x.AutomationId,
                    className = x.ClassName,
                    controlType = x.ControlType,
                    processName = x.ProcessName,
                    enabled = x.Enabled,
                    keyboardFocusable = x.KeyboardFocusable
                })
            })
        };

        if (!string.IsNullOrWhiteSpace(_apiKey))
            message.Headers.TryAddWithoutValidation("x-helpsys-key", _apiKey);

        using var response = await _http.SendAsync(message, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"HelpSys API {((int)response.StatusCode)}: {Short(body)}");

        var decision = JsonSerializer.Deserialize<GuideDecision>(body, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        return decision ?? throw new InvalidOperationException("HelpSys APIの応答を解析できませんでした。");
    }

    private static string Short(string value) => value.Length <= 180 ? value : value[..180];

    public void Dispose() => _http.Dispose();
}

using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using HelpSys.Models;

namespace HelpSys.Services;

public sealed class CloudGuideService : IDisposable
{
    private const string DefaultApiBase = "https://helpsys.syouziroupc.workers.dev";
    private static readonly HashSet<string> ShellProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "SearchHost", "StartMenuExperienceHost", "ShellExperienceHost", "TextInputHost", "ApplicationFrameHost"
    };

    private readonly HttpClient _http;
    private readonly SystemContextService _contextVerifier = new();
    private readonly string _apiBase;
    private readonly string? _apiKey;
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

    public CloudGuideService()
    {
        _apiBase = (Environment.GetEnvironmentVariable("HELPSYS_API_BASE") ?? DefaultApiBase).TrimEnd('/');
        _apiKey = Environment.GetEnvironmentVariable("HELPSYS_API_KEY");
        _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<GuideDecision> PlanAsync(string request, IReadOnlyList<UiElementCandidate> elements, IReadOnlyList<GuideHistoryItem> history, SystemContextSnapshot systemContext, CancellationToken cancellationToken = default)
    {
        var relevantElements = SelectRelevantElements(elements, systemContext);
        if (relevantElements.Count == 0)
            throw new GuideServiceException(GuideFailureKind.InvalidResponse, "前面アプリを特定できないため、UI候補を送信しません。");

        var body = new
        {
            request,
            history,
            systemContext,
            elements = relevantElements.Select(x => new
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

        EnsurePlanningContextCurrent(systemContext);
        var decision = await SendAsync<GuideDecision>(() => CreateMessage(HttpMethod.Post, $"{_apiBase}/v1/guide", body), cancellationToken);
        EnsurePlanningContextCurrent(systemContext);
        return decision;
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

        EnsurePlanningContextCurrent(systemContext);
        var decision = await SendAsync<VisionGuideDecision>(() => CreateMessage(HttpMethod.Post, $"{_apiBase}/v1/vision-guide", body), cancellationToken);
        EnsurePlanningContextCurrent(systemContext);
        return decision;
    }

    private static IReadOnlyList<UiElementCandidate> SelectRelevantElements(IReadOnlyList<UiElementCandidate> elements, SystemContextSnapshot systemContext)
    {
        var foregroundName = systemContext.ForegroundProcess ?? string.Empty;
        var foregroundId = systemContext.ForegroundProcessId;

        if (foregroundId <= 0 && string.IsNullOrWhiteSpace(foregroundName)) return [];

        return elements
            .Where(x =>
                (foregroundId > 0 && x.ProcessId == foregroundId) ||
                (!string.IsNullOrWhiteSpace(foregroundName) && x.ProcessName.Equals(foregroundName, StringComparison.OrdinalIgnoreCase)) ||
                ShellProcesses.Contains(x.ProcessName))
            .Take(420)
            .ToArray();
    }

    private void EnsurePlanningContextCurrent(SystemContextSnapshot expected)
    {
        var current = _contextVerifier.Capture();
        var foregroundChanged = expected.ForegroundProcessId <= 0 || current.ForegroundProcessId <= 0 ||
                                expected.ForegroundProcessId != current.ForegroundProcessId ||
                                !expected.ForegroundProcess.Equals(current.ForegroundProcess, StringComparison.OrdinalIgnoreCase);

        var expectedUrl = expected.Browser?.Url ?? string.Empty;
        var currentUrl = current.Browser?.Url ?? string.Empty;
        var browserChanged = !string.IsNullOrWhiteSpace(expectedUrl) && !string.IsNullOrWhiteSpace(currentUrl) &&
                             !expectedUrl.Equals(currentUrl, StringComparison.OrdinalIgnoreCase);

        if (foregroundChanged || browserChanged)
            throw new GuideServiceException(GuideFailureKind.ContextChanged, "操作中の画面が切り替わったため、古い案内応答を破棄しました。");
    }

    private HttpRequestMessage CreateMessage(HttpMethod method, string url, object body)
    {
        var message = new HttpRequestMessage(method, url) { Content = JsonContent.Create(body) };
        if (!string.IsNullOrWhiteSpace(_apiKey)) message.Headers.TryAddWithoutValidation("x-helpsys-key", _apiKey);
        return message;
    }

    private async Task<T> SendAsync<T>(Func<HttpRequestMessage> createMessage, CancellationToken cancellationToken)
    {
        GuideServiceException? lastTransientError = null;

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
                    try
                    {
                        return JsonSerializer.Deserialize<T>(body, _jsonOptions)
                               ?? throw new JsonException("empty response");
                    }
                    catch (JsonException ex)
                    {
                        throw new GuideServiceException(GuideFailureKind.InvalidResponse, "案内サービスの応答形式が不正です。", ex);
                    }
                }

                var status = (int)response.StatusCode;
                var kind = IsTransientStatus(status) ? GuideFailureKind.ServiceUnavailable : GuideFailureKind.Rejected;
                var apiError = new GuideServiceException(kind, $"HelpSys API {status}: {Short(body)}");
                if (!IsTransientStatus(status) || attempt > 0) throw apiError;
                lastTransientError = apiError;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
            catch (HttpRequestException ex)
            {
                var networkError = new GuideServiceException(GuideFailureKind.Network, "HelpSys APIへの通信に失敗しました。", ex);
                if (attempt > 0) throw networkError;
                lastTransientError = networkError;
            }

            await Task.Delay(350, cancellationToken);
        }

        throw lastTransientError ?? new GuideServiceException(GuideFailureKind.Network, "HelpSys APIへの通信に失敗しました。");
    }

    private static bool IsTransientStatus(int statusCode) => statusCode is 408 or 429 or 500 or 502 or 503 or 504;
    private static string Short(string value) => value.Length <= 180 ? value : value[..180];
    public void Dispose() => _http.Dispose();
}

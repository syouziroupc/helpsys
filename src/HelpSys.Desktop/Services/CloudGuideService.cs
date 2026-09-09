using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using HelpSys.Models;

namespace HelpSys.Services;

public sealed class CloudGuideService : IDisposable
{
    private const string DefaultApiBase = "https://helpsys.syouziroupc.workers.dev";
    private const string CloudflareCompatibleUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/142.0.0.0 Safari/537.36";
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(9);
    private const string InputPresentSentinel = "<input-present>";
    private static readonly HashSet<string> ShellProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "SearchHost", "StartMenuExperienceHost", "ShellExperienceHost", "TextInputHost", "ApplicationFrameHost"
    };
    private static readonly string[] SecurityTitleMarkers =
    [
        "privacy error", "deceptive site", "dangerous site", "smartscreen", "phishing", "malware",
        "安全ではありません", "安全でない", "フィッシング", "詐欺", "プライバシーが保護されません", "証明書エラー", "証明書が無効"
    ];

    private readonly HttpClient _http;
    private readonly SystemContextService _contextVerifier = new();
    private readonly UiAutomationScanner _stateVerifier = new();
    private readonly object _decisionMetadataGate = new();
    private readonly string _apiBase;
    private readonly string? _apiKey;
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };
    private long _rateLimitedUntilUtcTicks;
    private string? _lastInputTargetId;
    private string? _lastInputText;

    public CloudGuideService()
    {
        _apiBase = (Environment.GetEnvironmentVariable("HELPSYS_API_BASE") ?? DefaultApiBase).TrimEnd('/');
        _apiKey = Environment.GetEnvironmentVariable("HELPSYS_API_KEY");
        _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", CloudflareCompatibleUserAgent);
        _http.DefaultRequestHeaders.TryAddWithoutValidation("x-helpsys-client", "desktop");
    }

    public async Task<QualityGuideDecision> PlanQualityAsync(
        string request,
        ScreenCaptureFrame frame,
        IReadOnlyList<UiElementCandidate> elements,
        IReadOnlyList<GuideHistoryItem> history,
        SystemContextSnapshot systemContext,
        CancellationToken cancellationToken = default)
    {
        return await PlanQualityCoreAsync(request, frame, elements, history, systemContext, false, null, cancellationToken);
    }

    public async Task<QualityGuideDecision> PlanRecoveryAsync(
        string request,
        string routeIssue,
        ScreenCaptureFrame frame,
        IReadOnlyList<UiElementCandidate> elements,
        IReadOnlyList<GuideHistoryItem> history,
        SystemContextSnapshot systemContext,
        CancellationToken cancellationToken = default)
    {
        return await PlanQualityCoreAsync(request, frame, elements, history, systemContext, true, routeIssue, cancellationToken);
    }

    private async Task<QualityGuideDecision> PlanQualityCoreAsync(
        string request,
        ScreenCaptureFrame frame,
        IReadOnlyList<UiElementCandidate> elements,
        IReadOnlyList<GuideHistoryItem> history,
        SystemContextSnapshot systemContext,
        bool recoveryMode,
        string? routeIssue,
        CancellationToken cancellationToken)
    {
        ClearDecisionMetadata();
        var relevantElements = SelectRelevantElements(elements, systemContext);
        var cloudContext = SanitizeSystemContextForCloud(systemContext, request);
        var evidence = GuidanceEvidenceService.Build(true, relevantElements, history, cloudContext);
        var body = new
        {
            request,
            history,
            systemContext = cloudContext,
            evidence,
            captureBounds = new { x = frame.ScreenX, y = frame.ScreenY, width = frame.ScreenWidth, height = frame.ScreenHeight },
            recoveryMode,
            routeIssue = ShortValue(routeIssue),
            elements = relevantElements.Select(CompactElement),
            image = frame.ImageDataUri,
            imageWidth = frame.ImageWidth,
            imageHeight = frame.ImageHeight
        };

        EnsurePlanningContextCurrent(systemContext);
        var decision = await SendAsync<QualityGuideDecision>(
            () => CreateMessage(HttpMethod.Post, $"{_apiBase}/v1/quality-guide", body),
            cancellationToken);
        EnsurePlanningContextCurrent(systemContext);
        await EnsurePlanningEvidenceCurrentAsync(relevantElements, systemContext, cancellationToken);
        RememberDecisionMetadata(decision);
        return decision;
    }

    public async Task<GuideDecision> PlanAsync(string request, IReadOnlyList<UiElementCandidate> elements, IReadOnlyList<GuideHistoryItem> history, SystemContextSnapshot systemContext, CancellationToken cancellationToken = default)
    {
        ClearDecisionMetadata();
        var relevantElements = SelectRelevantElements(elements, systemContext);
        if (relevantElements.Count == 0)
            throw new GuideServiceException(GuideFailureKind.InvalidResponse, "前面アプリを特定できないため、UI候補を送信しません。");

        var cloudContext = SanitizeSystemContextForCloud(systemContext, request);
        var evidence = GuidanceEvidenceService.Build(false, relevantElements, history, cloudContext);
        var body = new
        {
            request,
            history,
            systemContext = cloudContext,
            evidence,
            elements = relevantElements.Select(CompactElement)
        };

        EnsurePlanningContextCurrent(systemContext);
        var decision = await SendAsync<GuideDecision>(() => CreateMessage(HttpMethod.Post, $"{_apiBase}/v1/guide", body), cancellationToken);
        EnsurePlanningContextCurrent(systemContext);
        await EnsurePlanningEvidenceCurrentAsync(relevantElements, systemContext, cancellationToken);
        RememberDecisionMetadata(decision);
        return decision;
    }

    public async Task<VisionGuideDecision> PlanVisionAsync(string request, ScreenCaptureFrame frame, IReadOnlyList<GuideHistoryItem> history, SystemContextSnapshot systemContext, CancellationToken cancellationToken = default)
    {
        ClearDecisionMetadata();
        var cloudContext = SanitizeSystemContextForCloud(systemContext, request);
        var evidence = GuidanceEvidenceService.Build(true, [], history, cloudContext);
        var body = new
        {
            request,
            history,
            systemContext = cloudContext,
            evidence,
            captureBounds = new { x = frame.ScreenX, y = frame.ScreenY, width = frame.ScreenWidth, height = frame.ScreenHeight },
            image = frame.ImageDataUri,
            imageWidth = frame.ImageWidth,
            imageHeight = frame.ImageHeight
        };

        EnsurePlanningContextCurrent(systemContext);
        var decision = await SendAsync<VisionGuideDecision>(() => CreateMessage(HttpMethod.Post, $"{_apiBase}/v1/vision-guide", body), cancellationToken);
        EnsurePlanningContextCurrent(systemContext);
        return decision;
    }

    public string? ResolveExpectedInputText(GuideDecision decision)
    {
        if (!decision.Action.Equals("type_text", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(decision.TargetId)) return null;
        lock (_decisionMetadataGate)
        {
            return string.Equals(_lastInputTargetId, decision.TargetId, StringComparison.Ordinal) ? _lastInputText : null;
        }
    }

    private void RememberDecisionMetadata(QualityGuideDecision decision)
    {
        lock (_decisionMetadataGate)
        {
            if (decision.Action.Equals("type_text", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(decision.TargetId) &&
                !string.IsNullOrWhiteSpace(decision.InputText))
            {
                _lastInputTargetId = decision.TargetId;
                _lastInputText = decision.InputText.Trim().Length <= 160 ? decision.InputText.Trim() : decision.InputText.Trim()[..160];
            }
            else
            {
                _lastInputTargetId = null;
                _lastInputText = null;
            }
        }
    }

    private void RememberDecisionMetadata(GuideDecision decision)
    {
        lock (_decisionMetadataGate)
        {
            if (decision.Action.Equals("type_text", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(decision.TargetId) &&
                !string.IsNullOrWhiteSpace(decision.InputText))
            {
                _lastInputTargetId = decision.TargetId;
                _lastInputText = decision.InputText.Trim().Length <= 160 ? decision.InputText.Trim() : decision.InputText.Trim()[..160];
            }
            else
            {
                _lastInputTargetId = null;
                _lastInputText = null;
            }
        }
    }

    private void ClearDecisionMetadata()
    {
        lock (_decisionMetadataGate)
        {
            _lastInputTargetId = null;
            _lastInputText = null;
        }
    }

    private static object CompactElement(UiElementCandidate x) => new
    {
        id = x.Id,
        name = SanitizeElementNameForCloud(x),
        automationId = x.AutomationId,
        className = x.ClassName,
        controlType = x.ControlType,
        processName = x.ProcessName,
        interactable = x.Interactable,
        enabled = x.Enabled,
        keyboardFocusable = x.KeyboardFocusable,
        focused = x.Focused,
        password = x.Password,
        value = x.Password || string.IsNullOrEmpty(x.Value) ? null : InputPresentSentinel,
        toggleState = x.ToggleState,
        selected = x.Selected,
        expandCollapseState = x.ExpandCollapseState,
        x = x.X,
        y = x.Y,
        width = x.Width,
        height = x.Height
    };

    private static string SanitizeElementNameForCloud(UiElementCandidate element)
    {
        if (element.ControlType.Equals("Window", StringComparison.OrdinalIgnoreCase) ||
            element.ControlType.Equals("TitleBar", StringComparison.OrdinalIgnoreCase))
            return string.IsNullOrWhiteSpace(element.ProcessName) ? "<window>" : element.ProcessName;
        return element.Name;
    }

    private static SystemContextSnapshot SanitizeSystemContextForCloud(SystemContextSnapshot context, string request)
    {
        var safeForegroundTitle = SanitizeWindowTitleForCloud(context.ForegroundProcess, context.ForegroundTitle);
        if (context.Browser is null)
            return context with { ForegroundTitle = safeForegroundTitle };

        var browser = context.Browser with
        {
            Url = SanitizeBrowserUrl(context.Browser.Url, KnownSiteSearchMarker(request)),
            WindowTitle = SanitizeWindowTitleForCloud(context.Browser.ProcessName, context.Browser.WindowTitle)
        };
        return context with { ForegroundTitle = safeForegroundTitle, Browser = browser };
    }

    private static string SanitizeWindowTitleForCloud(string processName, string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return string.Empty;
        var raw = title.Trim();

        if (SecurityTitleMarkers.Any(marker => raw.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            return "Privacy error";

        return string.IsNullOrWhiteSpace(processName) ? "<window-title-present>" : processName.Trim();
    }

    private static string? SanitizeBrowserUrl(string? value, string? knownSiteSearchMarker)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var raw = value.Trim();

        if (Uri.TryCreate(raw, UriKind.Absolute, out var uri) &&
            (uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) ||
             uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)))
        {
            var searchLike = !string.IsNullOrWhiteSpace(uri.Query) ||
                             uri.AbsolutePath.Contains("search", StringComparison.OrdinalIgnoreCase);
            var host = uri.IdnHost.ToLowerInvariant();
            var port = uri.IsDefaultPort ? string.Empty : $":{uri.Port}";
            var marker = string.Empty;
            if (searchLike)
            {
                marker = "?search=1";
                if (!string.IsNullOrWhiteSpace(knownSiteSearchMarker))
                    marker += $"&term={Uri.EscapeDataString(knownSiteSearchMarker)}";
            }
            return $"{uri.Scheme.ToLowerInvariant()}://{host}{port}/{marker}";
        }

        var cut = raw.IndexOfAny(['?', '#']);
        if (cut >= 0) raw = raw[..cut];
        return raw.Length <= 180 ? raw : raw[..180];
    }

    private static string? KnownSiteSearchMarker(string request)
    {
        if (string.IsNullOrWhiteSpace(request)) return null;
        if (request.Contains("youtube", StringComparison.OrdinalIgnoreCase) || request.Contains("ユーチューブ", StringComparison.OrdinalIgnoreCase))
            return "YouTube";
        if (request.Contains("楽天市場", StringComparison.OrdinalIgnoreCase) || request.Contains("楽天", StringComparison.OrdinalIgnoreCase) || request.Contains("rakuten", StringComparison.OrdinalIgnoreCase))
            return "楽天市場";
        if (request.Contains("yahoo", StringComparison.OrdinalIgnoreCase) || request.Contains("ヤフー", StringComparison.OrdinalIgnoreCase))
            return "Yahoo JAPAN";
        if (request.Contains("amazon", StringComparison.OrdinalIgnoreCase) || request.Contains("アマゾン", StringComparison.OrdinalIgnoreCase))
            return "Amazon";
        if (request.Contains("google map", StringComparison.OrdinalIgnoreCase) || request.Contains("googleマップ", StringComparison.OrdinalIgnoreCase) || request.Contains("グーグルマップ", StringComparison.OrdinalIgnoreCase))
            return "Google マップ";
        return null;
    }

    private static string? ShortValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        return text.Length <= 180 ? text : text[..180];
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
            .Take(280)
            .ToArray();
    }

    private async Task EnsurePlanningEvidenceCurrentAsync(
        IReadOnlyList<UiElementCandidate> expectedElements,
        SystemContextSnapshot expectedContext,
        CancellationToken cancellationToken)
    {
        var processId = expectedContext.ForegroundProcessId;
        if (processId <= 0) throw new GuideServiceException(GuideFailureKind.ContextChanged, "判断後に前面画面を再確認できませんでした。");

        var expected = expectedElements.Where(x => x.ProcessId == processId).Take(280).ToArray();
        if (expected.Length == 0) return;

        IReadOnlyList<UiElementCandidate> current;
        try
        {
            current = await _stateVerifier.CaptureCandidatesForProcessAsync(processId, 420, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            throw new GuideServiceException(GuideFailureKind.ContextChanged, "判断後の画面構造を再確認できないため、古い案内応答を使いません。", ex);
        }

        if (current.Count == 0)
            throw new GuideServiceException(GuideFailureKind.ContextChanged, "判断後の画面構造を再確認できないため、古い案内応答を使いません。");

        if (HasPlanningWindowSetChanged(expected, current) || HasPlanningSemanticStateChanged(expected, current))
            throw new GuideServiceException(GuideFailureKind.ContextChanged, "AIが判断している間に同じアプリ内の画面状態が変わったため、古い案内応答を破棄しました。");
    }

    private static bool HasPlanningWindowSetChanged(
        IReadOnlyList<UiElementCandidate> expected,
        IReadOnlyList<UiElementCandidate> current)
    {
        var before = expected.Where(x => x.ControlType.Equals("Window", StringComparison.OrdinalIgnoreCase))
            .Select(PlanningWindowKey).ToHashSet(StringComparer.Ordinal);
        var after = current.Where(x => x.ControlType.Equals("Window", StringComparison.OrdinalIgnoreCase))
            .Select(PlanningWindowKey).ToHashSet(StringComparer.Ordinal);
        return !before.SetEquals(after);
    }

    private static bool HasPlanningSemanticStateChanged(
        IReadOnlyList<UiElementCandidate> expected,
        IReadOnlyList<UiElementCandidate> current)
    {
        var before = PlanningSemanticMap(expected);
        var after = PlanningSemanticMap(current);
        foreach (var pair in before)
        {
            if (after.TryGetValue(pair.Key, out var now) && !pair.Value.Equals(now, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static Dictionary<string, string> PlanningSemanticMap(IReadOnlyList<UiElementCandidate> elements)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in elements.Where(x => x.Interactable))
        {
            var identity = PlanningElementIdentity(item);
            if (map.ContainsKey(identity)) continue;
            map[identity] = $"focus={item.Focused};toggle={item.ToggleState ?? string.Empty};selected={item.Selected?.ToString() ?? string.Empty};expand={item.ExpandCollapseState ?? string.Empty}";
        }
        return map;
    }

    private static string PlanningWindowKey(UiElementCandidate x)
    {
        var bx = (int)Math.Round(x.X / 24d);
        var by = (int)Math.Round(x.Y / 24d);
        var bw = (int)Math.Round(x.Width / 24d);
        var bh = (int)Math.Round(x.Height / 24d);
        return $"{x.ProcessId}|{x.AutomationId}|{x.ClassName}|{bx},{by},{bw},{bh}";
    }

    private static string PlanningElementIdentity(UiElementCandidate x)
    {
        var bx = (int)Math.Round(x.X / 24d);
        var by = (int)Math.Round(x.Y / 24d);
        var bw = (int)Math.Round(x.Width / 24d);
        var bh = (int)Math.Round(x.Height / 24d);
        var stableName = x.ControlType.ToLowerInvariant() is
            "button" or "menuitem" or "listitem" or "treeitem" or "tabitem" or "hyperlink" or "checkbox" or "radiobutton"
            ? NormalizePlanningName(x.Name)
            : string.Empty;
        return $"{x.ProcessId}|{x.ControlType}|{x.AutomationId}|{x.ClassName}|{stableName}|{bx},{by},{bw},{bh}";
    }

    private static string NormalizePlanningName(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var normalized = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= 72 ? normalized : normalized[..72];
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
        message.Headers.TryAddWithoutValidation("x-helpsys-request-id", Guid.NewGuid().ToString("N"));
        if (!string.IsNullOrWhiteSpace(_apiKey)) message.Headers.TryAddWithoutValidation("x-helpsys-key", _apiKey);
        return message;
    }

    private async Task<T> SendAsync<T>(Func<HttpRequestMessage> createMessage, CancellationToken cancellationToken)
    {
        ThrowIfLocallyRateLimited();
        GuideServiceException? lastTransientError = null;

        for (var attempt = 0; attempt < 2; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfLocallyRateLimited();
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attemptCts.CancelAfter(AttemptTimeout);

            try
            {
                using var message = createMessage();
                using var response = await _http.SendAsync(message, attemptCts.Token);
                var body = await response.Content.ReadAsStringAsync(attemptCts.Token);

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
                if (status == 429)
                {
                    RememberRateLimit(response);
                    throw new GuideServiceException(GuideFailureKind.ServiceUnavailable, "案内サービスの利用が集中しています。同じ要求を連続送信せず、制限解除後に再開します。");
                }

                var kind = IsTransientStatus(status) ? GuideFailureKind.ServiceUnavailable : GuideFailureKind.Rejected;
                var apiError = new GuideServiceException(kind, $"HelpSys API {status}: {Short(body)}");
                if (!IsTransientStatus(status) || attempt > 0) throw apiError;
                lastTransientError = apiError;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                var timeoutError = new GuideServiceException(GuideFailureKind.ServiceUnavailable, "案内モデルの応答が9秒を超えました。");
                if (attempt > 0) throw timeoutError;
                lastTransientError = timeoutError;
            }
            catch (HttpRequestException ex)
            {
                var networkError = new GuideServiceException(GuideFailureKind.Network, "HelpSys APIへの通信に失敗しました。", ex);
                if (attempt > 0) throw networkError;
                lastTransientError = networkError;
            }

            await Task.Delay(250, cancellationToken);
        }

        throw lastTransientError ?? new GuideServiceException(GuideFailureKind.Network, "HelpSys APIへの通信に失敗しました。");
    }

    private void RememberRateLimit(HttpResponseMessage response)
    {
        var wait = TimeSpan.FromSeconds(60);
        if (response.Headers.RetryAfter?.Delta is { } delta)
        {
            wait = delta;
        }
        else if (response.Headers.RetryAfter?.Date is { } date)
        {
            wait = date - DateTimeOffset.UtcNow;
        }
        wait = TimeSpan.FromSeconds(Math.Clamp(wait.TotalSeconds, 1, 300));
        Interlocked.Exchange(ref _rateLimitedUntilUtcTicks, DateTime.UtcNow.Add(wait).Ticks);
    }

    private void ThrowIfLocallyRateLimited()
    {
        var untilTicks = Interlocked.Read(ref _rateLimitedUntilUtcTicks);
        if (untilTicks <= DateTime.UtcNow.Ticks) return;
        throw new GuideServiceException(GuideFailureKind.ServiceUnavailable, "案内サービスの利用制限が解除されるまで、同じ要求の再送を停止しています。");
    }

    private static bool IsTransientStatus(int statusCode) => statusCode is 408 or 500 or 502 or 503 or 504;
    private static string Short(string value) => value.Length <= 180 ? value : value[..180];
    public void Dispose() => _http.Dispose();
}

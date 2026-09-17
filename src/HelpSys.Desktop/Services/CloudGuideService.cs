using System.Net.Http;
using System.Text.Json;
using HelpSys.Models;
using HelpSys.Shared;

namespace HelpSys.Services;

public sealed class CloudGuideService : IDisposable
{
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(30);
    private static readonly HashSet<string> ShellProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "SearchHost", "StartMenuExperienceHost", "ShellExperienceHost", "TextInputHost", "ApplicationFrameHost"
    };

    private SystemContextService _contextVerifier = new();
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly PrivacyGate _privacyGate;
    private readonly CloudAiAdapter _adapter;
    private readonly bool _ownsAdapter;

    public CloudGuideService(PrivacyGate? privacyGate = null, CloudAiAdapter? adapter = null)
    {
        _privacyGate = privacyGate ?? new PrivacyGate();
        _adapter = adapter ?? new CloudAiAdapter();
        _ownsAdapter = adapter is null;
    }

    public event Action<PrivacyAssessment>? PrivacyBlocked;
    public PrivacyGate PrivacyGate => _privacyGate;
    public bool CloudEndpointConfigured => _adapter.IsConfigured;

    public void UseContextVerifier(SystemContextService contextVerifier)
    {
        _contextVerifier = contextVerifier ?? throw new ArgumentNullException(nameof(contextVerifier));
    }

    public PrivacyAssessment PreflightPrivacy(
        SystemContextSnapshot systemContext,
        IReadOnlyList<UiElementCandidate> elements)
    {
        if (!CloudEndpointConfigured)
        {
            var unavailable = new PrivacyAssessment(
                PrivacyClassification.Unknown,
                "cloud_endpoint_unconfigured",
                "安全版の承認済みAI API接続先が設定されていないため、画面画像を取得せず外部送信を停止しています。");
            PrivacyBlocked?.Invoke(unavailable);
            return unavailable;
        }

        var relevantElements = SelectRelevantElements(elements, systemContext);
        var assessment = _privacyGate.EvaluateState(systemContext, relevantElements);
        if (!assessment.CanSend) PrivacyBlocked?.Invoke(assessment);
        return assessment;
    }

    public Task<QualityGuideDecision> PlanQualityAsync(
        string request,
        ScreenCaptureFrame frame,
        IReadOnlyList<UiElementCandidate> elements,
        IReadOnlyList<GuideHistoryItem> history,
        SystemContextSnapshot systemContext,
        CancellationToken cancellationToken = default)
        => PlanQualityCoreAsync(request, frame, elements, history, systemContext, false, null, cancellationToken);

    public Task<QualityGuideDecision> PlanRecoveryAsync(
        string request,
        string routeIssue,
        ScreenCaptureFrame frame,
        IReadOnlyList<UiElementCandidate> elements,
        IReadOnlyList<GuideHistoryItem> history,
        SystemContextSnapshot systemContext,
        CancellationToken cancellationToken = default)
        => PlanQualityCoreAsync(request, frame, elements, history, systemContext, true, routeIssue, cancellationToken);

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
        EnsureCloudConfigured();
        var relevantElements = SelectRelevantElements(elements, systemContext, request);
        var approval = _privacyGate.ApproveQuality(request, frame, relevantElements, history, systemContext, recoveryMode, routeIssue);
        EnsureApproved(approval);

        EnsurePlanningContextCurrent(systemContext, requireSameWindow: true);
        var decision = await SendAsync<QualityGuideDecision>("/v1/quality-guide", approval.Body!, cancellationToken);
        EnsurePlanningContextCurrent(systemContext, requireSameWindow: false);
        return decision;
    }

    public async Task<GuideDecision> PlanAsync(
        string request,
        IReadOnlyList<UiElementCandidate> elements,
        IReadOnlyList<GuideHistoryItem> history,
        SystemContextSnapshot systemContext,
        CancellationToken cancellationToken = default)
    {
        EnsureCloudConfigured();
        var relevantElements = SelectRelevantElements(elements, systemContext, request);
        if (relevantElements.Count == 0)
            throw new GuideServiceException(GuideFailureKind.InvalidResponse, "前面アプリを特定できないため、UI候補を送信しません。");

        var approval = _privacyGate.ApproveStructured(request, relevantElements, history, systemContext);
        EnsureApproved(approval);

        EnsurePlanningContextCurrent(systemContext, requireSameWindow: true);
        var decision = await SendAsync<GuideDecision>("/v1/guide", approval.Body!, cancellationToken);
        EnsurePlanningContextCurrent(systemContext, requireSameWindow: false);
        return decision;
    }

    public Task<VisionGuideDecision> PlanVisionAsync(
        string request,
        ScreenCaptureFrame frame,
        IReadOnlyList<GuideHistoryItem> history,
        SystemContextSnapshot systemContext,
        CancellationToken cancellationToken = default)
        => PlanVisionAsync(request, frame, [], history, systemContext, cancellationToken);

    public async Task<VisionGuideDecision> PlanVisionAsync(
        string request,
        ScreenCaptureFrame frame,
        IReadOnlyList<UiElementCandidate> elements,
        IReadOnlyList<GuideHistoryItem> history,
        SystemContextSnapshot systemContext,
        CancellationToken cancellationToken = default)
    {
        EnsureCloudConfigured();
        var relevantElements = SelectRelevantElements(elements, systemContext, request);
        var approval = _privacyGate.ApproveVision(request, frame, relevantElements, history, systemContext);
        EnsureApproved(approval);

        EnsurePlanningContextCurrent(systemContext, requireSameWindow: true);
        var decision = await SendAsync<VisionGuideDecision>("/v1/vision-guide", approval.Body!, cancellationToken);
        EnsurePlanningContextCurrent(systemContext, requireSameWindow: false);
        return decision;
    }

    private void EnsureCloudConfigured()
    {
        if (CloudEndpointConfigured) return;
        throw new GuideServiceException(
            GuideFailureKind.Rejected,
            "安全版の承認済みAI API接続先が設定されていないため、外部送信を拒否しました。");
    }

    private void EnsureApproved(PrivacyApproval approval)
    {
        if (approval.CanSend) return;
        PrivacyBlocked?.Invoke(approval.Assessment);
        throw new GuideServiceException(GuideFailureKind.PrivacyBlocked, approval.Assessment.UserMessage);
    }

    private static IReadOnlyList<UiElementCandidate> SelectRelevantElements(
        IReadOnlyList<UiElementCandidate> elements,
        SystemContextSnapshot systemContext,
        string? request = null)
    {
        var foregroundName = systemContext.ForegroundProcess ?? string.Empty;
        var foregroundId = systemContext.ForegroundProcessId;
        if (foregroundId <= 0 && string.IsNullOrWhiteSpace(foregroundName)) return [];

        var relevant = elements
            .Where(x =>
                (foregroundId > 0 && x.ProcessId == foregroundId) ||
                (!string.IsNullOrWhiteSpace(foregroundName) && x.ProcessName.Equals(foregroundName, StringComparison.OrdinalIgnoreCase)) ||
                ShellProcesses.Contains(x.ProcessName))
            .ToArray();

        relevant = FilterWindowChromeForTask(relevant, request);

        var office = foregroundName.Equals("excel", StringComparison.OrdinalIgnoreCase) ||
                     foregroundName.Equals("winword", StringComparison.OrdinalIgnoreCase) ||
                     foregroundName.Equals("powerpnt", StringComparison.OrdinalIgnoreCase);
        var contextBudget = systemContext.Browser is not null ? 110 : office ? 100 : 60;
        var interactiveBudget = 280 - contextBudget;

        var selected = new List<UiElementCandidate>(280);
        selected.AddRange(relevant
            .Where(x => x.Interactable)
            .OrderByDescending(x => x.Focused)
            .ThenByDescending(x => x.Enabled)
            .ThenByDescending(x => !string.IsNullOrWhiteSpace(x.Name))
            .Take(interactiveBudget));

        selected.AddRange(relevant
            .Where(x => !x.Interactable && !string.IsNullOrWhiteSpace(x.Name))
            .OrderByDescending(ContextPriority)
            .Take(contextBudget));

        if (selected.Count < 280)
        {
            var selectedIds = selected.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
            selected.AddRange(relevant
                .Where(x => !selectedIds.Contains(x.Id))
                .Take(280 - selected.Count));
        }

        return selected.Take(280).ToArray();
    }

    private static UiElementCandidate[] FilterWindowChromeForTask(
        UiElementCandidate[] elements,
        string? request)
    {
        if (elements.Length == 0 || IsWindowManagementRequest(request)) return elements;

        var topByProcess = elements
            .Where(x => x.ProcessId > 0 && !x.Bounds.IsEmpty)
            .GroupBy(x => x.ProcessId)
            .ToDictionary(group => group.Key, group => group.Min(x => x.Y));

        return elements
            .Where(x => !IsWindowChromeControl(x, topByProcess))
            .ToArray();
    }

    private static bool IsWindowChromeControl(
        UiElementCandidate item,
        IReadOnlyDictionary<int, double> topByProcess)
    {
        if (!item.Interactable || item.ProcessId <= 0 || item.Bounds.IsEmpty) return false;
        if (!topByProcess.TryGetValue(item.ProcessId, out var top)) return false;
        if (item.Y > top + 72) return false;

        var automationId = item.AutomationId?.Trim() ?? string.Empty;
        var name = item.Name?.Trim() ?? string.Empty;

        if (item.ControlType.Equals("Button", StringComparison.OrdinalIgnoreCase) &&
            (automationId.Equals("Minimize", StringComparison.OrdinalIgnoreCase) ||
             automationId.Equals("Maximize", StringComparison.OrdinalIgnoreCase) ||
             automationId.Equals("Restore", StringComparison.OrdinalIgnoreCase) ||
             automationId.Equals("Close", StringComparison.OrdinalIgnoreCase)))
            return true;

        return item.ControlType.Equals("MenuItem", StringComparison.OrdinalIgnoreCase) &&
               automationId.StartsWith("Item ", StringComparison.OrdinalIgnoreCase) &&
               (name.Equals("System", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("システム", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("システム メニュー", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsWindowManagementRequest(string? request)
    {
        if (string.IsNullOrWhiteSpace(request)) return false;
        var text = request.Trim();
        string[] terms =
        [
            "最小化", "最大化", "元のサイズ", "ウィンドウサイズ", "ウィンドウを閉", "画面を閉", "閉じる", "閉じて",
            "minimize", "maximise", "maximize", "restore window", "close window", "window size"
        ];
        return terms.Any(term => text.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static int ContextPriority(UiElementCandidate item)
    {
        var score = item.ControlType switch
        {
            "Document" => 120,
            "Text" => 105,
            "DataItem" => 100,
            "Table" => 90,
            "Hyperlink" => 90,
            "Group" => 60,
            "Pane" => 50,
            "Window" => 40,
            _ => 10
        };
        if (!string.IsNullOrWhiteSpace(item.Name)) score += Math.Min(30, item.Name.Length / 12);
        if (item.Focused) score += 40;
        return score;
    }

    private void EnsurePlanningContextCurrent(SystemContextSnapshot expected, bool requireSameWindow)
    {
        var current = _contextVerifier.Capture();
        var foregroundChanged = expected.ForegroundProcessId <= 0 || current.ForegroundProcessId <= 0 ||
                                expected.ForegroundProcessId != current.ForegroundProcessId ||
                                !expected.ForegroundProcess.Equals(current.ForegroundProcess, StringComparison.OrdinalIgnoreCase);

        var windowChanged = requireSameWindow &&
                            (expected.ForegroundWindowHandle == nint.Zero ||
                             current.ForegroundWindowHandle == nint.Zero ||
                             expected.ForegroundWindowHandle != current.ForegroundWindowHandle);

        var expectedDomain = expected.Browser?.Domain ?? string.Empty;
        var currentDomain = current.Browser?.Domain ?? string.Empty;
        var browserChanged = !string.IsNullOrWhiteSpace(expectedDomain) && !string.IsNullOrWhiteSpace(currentDomain) &&
                             !expectedDomain.Equals(currentDomain, StringComparison.OrdinalIgnoreCase);

        if (foregroundChanged || windowChanged || browserChanged)
            throw new GuideServiceException(GuideFailureKind.ContextChanged, "操作中の画面が切り替わったため、古い案内応答を破棄しました。");
    }

    private async Task<T> SendAsync<T>(string path, object body, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var response = await _adapter.PostJsonAsync(path, body, AttemptTimeout, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                try
                {
                    return JsonSerializer.Deserialize<T>(response.Body, _jsonOptions)
                           ?? throw new JsonException("empty response");
                }
                catch (JsonException ex)
                {
                    throw new GuideServiceException(GuideFailureKind.InvalidResponse, "案内サービスの応答形式が不正です。", ex);
                }
            }

            var kind = IsTransientStatus(response.StatusCode) ? GuideFailureKind.ServiceUnavailable : GuideFailureKind.Rejected;
            throw new GuideServiceException(kind, $"HelpSys API {response.StatusCode}: {Short(response.Body)}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw new GuideServiceException(GuideFailureKind.ServiceUnavailable, "Gemini案内モデルの応答が30秒を超えました。");
        }
        catch (HttpRequestException ex)
        {
            throw new GuideServiceException(GuideFailureKind.Network, "HelpSys APIへの通信に失敗しました。", ex);
        }
    }

    private static bool IsTransientStatus(int statusCode) => statusCode is 408 or 429 or 500 or 502 or 503 or 504;
    private static string Short(string value) => value.Length <= 180 ? value : value[..180];

    public void Dispose()
    {
        if (_ownsAdapter) _adapter.Dispose();
    }
}

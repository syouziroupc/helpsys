using System.Net.Http;
using System.Text.Json;
using HelpSys.Models;
using HelpSys.Shared;

namespace HelpSys.Services;

public sealed class CloudGuideService : IDisposable
{
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(9);
    private static readonly HashSet<string> ShellProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "SearchHost", "StartMenuExperienceHost", "ShellExperienceHost", "TextInputHost", "ApplicationFrameHost"
    };

    private readonly SystemContextService _contextVerifier = new();
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

    /// <summary>
    /// Lightweight local check used before a screenshot is even created. A blocked, unknown, or
    /// unconfigured Safe cloud state enters Privacy Mode immediately. The send-time gate still runs
    /// again as a TOCTOU guard.
    /// </summary>
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
        var relevantElements = SelectRelevantElements(elements, systemContext);
        var approval = _privacyGate.ApproveQuality(request, frame, relevantElements, history, systemContext, recoveryMode, routeIssue);
        EnsureApproved(approval);

        EnsurePlanningContextCurrent(systemContext);
        var decision = await SendAsync<QualityGuideDecision>("/v1/quality-guide", approval.Body!, cancellationToken);
        EnsurePlanningContextCurrent(systemContext);
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
        var relevantElements = SelectRelevantElements(elements, systemContext);
        if (relevantElements.Count == 0)
            throw new GuideServiceException(GuideFailureKind.InvalidResponse, "前面アプリを特定できないため、UI候補を送信しません。");

        var approval = _privacyGate.ApproveStructured(request, relevantElements, history, systemContext);
        EnsureApproved(approval);

        EnsurePlanningContextCurrent(systemContext);
        var decision = await SendAsync<GuideDecision>("/v1/guide", approval.Body!, cancellationToken);
        EnsurePlanningContextCurrent(systemContext);
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
        var relevantElements = SelectRelevantElements(elements, systemContext);
        var approval = _privacyGate.ApproveVision(request, frame, relevantElements, history, systemContext);
        EnsureApproved(approval);

        EnsurePlanningContextCurrent(systemContext);
        var decision = await SendAsync<VisionGuideDecision>("/v1/vision-guide", approval.Body!, cancellationToken);
        EnsurePlanningContextCurrent(systemContext);
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
        SystemContextSnapshot systemContext)
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

        var contextBudget = systemContext.Browser is null ? 45 : 80;
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

    private static int ContextPriority(UiElementCandidate item)
    {
        var score = item.ControlType switch
        {
            "Document" => 90,
            "Text" => 80,
            "Group" => 60,
            "Pane" => 50,
            "Window" => 40,
            _ => 10
        };
        if (!string.IsNullOrWhiteSpace(item.Name)) score += Math.Min(30, item.Name.Length / 12);
        if (item.Focused) score += 40;
        return score;
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

    private async Task<T> SendAsync<T>(string path, object body, CancellationToken cancellationToken)
    {
        GuideServiceException? lastTransientError = null;

        for (var attempt = 0; attempt < 2; attempt++)
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
                var apiError = new GuideServiceException(kind, $"HelpSys API {response.StatusCode}: {Short(response.Body)}");
                if (!IsTransientStatus(response.StatusCode) || attempt > 0) throw apiError;
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

    private static bool IsTransientStatus(int statusCode) => statusCode is 408 or 429 or 500 or 502 or 503 or 504;
    private static string Short(string value) => value.Length <= 180 ? value : value[..180];

    public void Dispose()
    {
        if (_ownsAdapter) _adapter.Dispose();
    }
}

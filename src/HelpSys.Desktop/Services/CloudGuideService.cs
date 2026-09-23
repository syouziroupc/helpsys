using System.Net.Http;
using System.Text.Json;
using HelpSys.Models;
using HelpSys.Shared;

namespace HelpSys.Services;

public sealed class CloudGuideService : IDisposable
{
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(6);
    private static readonly HashSet<string> ShellProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "SearchHost", "StartMenuExperienceHost", "ShellExperienceHost", "TextInputHost", "ApplicationFrameHost"
    };

    private SystemContextService? _contextVerifier;
    private Func<long>? _privacyEpochProvider;
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly PrivacyGate _privacyGate;
    private readonly CloudAiAdapter _adapter;
    private readonly bool _ownsAdapter;
    private readonly object _circuitGate = new();
    private int _consecutiveTransientFailures;
    private DateTime _circuitOpenUntilUtc = DateTime.MinValue;
    private const int CircuitFailureThreshold = 3;
    private static readonly TimeSpan CircuitOpenDuration = TimeSpan.FromSeconds(8);

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

    public void UsePrivacyEpochProvider(Func<long> privacyEpochProvider)
    {
        _privacyEpochProvider = privacyEpochProvider ?? throw new ArgumentNullException(nameof(privacyEpochProvider));
    }

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
        => PlanQualityCoreAsync(request, frame, elements, history, systemContext, cancellationToken);

    private async Task<QualityGuideDecision> PlanQualityCoreAsync(
        string request,
        ScreenCaptureFrame frame,
        IReadOnlyList<UiElementCandidate> elements,
        IReadOnlyList<GuideHistoryItem> history,
        SystemContextSnapshot systemContext,
        CancellationToken cancellationToken)
    {
        if (OutlawModePolicy.Enabled)
            return await PlanOutlawQualityAsync(request, frame, elements, history, systemContext, cancellationToken);

        EnsureCloudConfigured();
        var privacyEpoch = CapturePrivacyEpoch();
        var relevantElements = SelectRelevantElements(elements, systemContext, request);
        var approval = _privacyGate.ApproveQuality(request, frame, relevantElements, history, systemContext, false, null);
        EnsureApproved(approval);

        EnsurePlanningContextCurrent(systemContext);
        EnsurePrivacyEpochCurrent(privacyEpoch);

        QualityGuideDecision decision;
        try
        {
            decision = await SendAsync<QualityGuideDecision>("/v2/plan", approval.Body!, cancellationToken);
        }
        catch (GuideServiceException ex) when (IsUnsupportedUnifiedRoute(ex))
        {
            // Temporary production compatibility: the currently deployed Stable 3.0 Worker
            // exposes /v1/plan rather than /v2/plan. Re-run the same current observation through
            // a privacy-approved compatibility payload; this is a route/schema adapter, not a
            // second planning strategy.
            var stableApproval = _privacyGate.ApproveStablePlanCompatibility(
                request,
                frame,
                relevantElements,
                systemContext);
            EnsureApproved(stableApproval);
            EnsurePlanningContextCurrent(systemContext);
            EnsurePrivacyEpochCurrent(privacyEpoch);

            var legacy = await SendAsync<StablePlanCompatibilityResponse>(
                "/v1/plan",
                stableApproval.Body!,
                cancellationToken,
                TimeSpan.FromSeconds(28));

            var visualOnly =
                string.IsNullOrWhiteSpace(legacy.TargetId) &&
                legacy.Action is "left_click" or "double_click" &&
                legacy.Width > 0 &&
                legacy.Height > 0;

            decision = new QualityGuideDecision(
                legacy.Status,
                visualOnly ? "vision-target" : legacy.TargetId,
                legacy.Action,
                legacy.Instruction,
                legacy.Question,
                legacy.Key,
                legacy.Confidence,
                legacy.X,
                legacy.Y,
                legacy.Width,
                legacy.Height,
                true,
                "current screenshot + UI Automation (Stable 3.0 compatibility route)");
        }

        EnsurePlanningContextCurrent(systemContext);
        EnsurePrivacyEpochCurrent(privacyEpoch);
        return decision;
    }

    private async Task<QualityGuideDecision> PlanOutlawQualityAsync(
        string request,
        ScreenCaptureFrame frame,
        IReadOnlyList<UiElementCandidate> elements,
        IReadOnlyList<GuideHistoryItem> history,
        SystemContextSnapshot systemContext,
        CancellationToken cancellationToken)
    {
        EnsureCloudConfigured();

        var evidence = GuidanceEvidenceService.Build(true, elements, history, systemContext);
        var body = new
        {
            request,
            history,
            systemContext = new
            {
                systemContext.ForegroundProcess,
                systemContext.ForegroundTitle,
                systemContext.ForegroundProcessId,
                foregroundWindowHandle = systemContext.ForegroundWindowHandle.ToInt64(),
                systemContext.TaskbarVisible,
                runningApps = systemContext.RunningApps,
                browser = systemContext.Browser
            },
            evidence,
            elements,
            image = frame.ImageDataUri,
            capture = new
            {
                frame.ScreenX,
                frame.ScreenY,
                frame.ScreenWidth,
                frame.ScreenHeight,
                frame.ImageWidth,
                frame.ImageHeight
            }
        };

        CloudAiResponse response;
        try
        {
            response = await PerformanceTrace.MeasureAsync(
                "cloud.outlaw-plan",
                () => _adapter.PostJsonAsync(
                    "/v1/outlaw-plan",
                    body,
                    TimeSpan.FromSeconds(80),
                    cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            throw new GuideServiceException(
                GuideFailureKind.ServiceUnavailable,
                "無法者版GLMの判断が80秒以内に完了しませんでした。",
                ex);
        }
        catch (HttpRequestException ex)
        {
            throw new GuideServiceException(
                GuideFailureKind.Network,
                "無法者版GLM APIへの通信に失敗しました。",
                ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            var kind = IsTransientStatus(response.StatusCode)
                ? GuideFailureKind.ServiceUnavailable
                : GuideFailureKind.Rejected;
            throw new GuideServiceException(
                kind,
                $"Outlaw API {response.StatusCode}: {Short(response.Body)}");
        }

        try
        {
            return JsonSerializer.Deserialize<QualityGuideDecision>(response.Body, _jsonOptions)
                   ?? throw new JsonException("empty response");
        }
        catch (JsonException ex)
        {
            throw new GuideServiceException(
                GuideFailureKind.InvalidResponse,
                "無法者版GLMの応答形式が不正です。",
                ex);
        }
    }

    public async Task<GuideDecision> PlanAsync(
        string request,
        IReadOnlyList<UiElementCandidate> elements,
        IReadOnlyList<GuideHistoryItem> history,
        SystemContextSnapshot systemContext,
        CancellationToken cancellationToken = default)
    {
        if (OutlawModePolicy.Enabled)
            return await PlanOutlawStructuredAsync(request, elements, history, systemContext, cancellationToken);

        EnsureCloudConfigured();
        var privacyEpoch = CapturePrivacyEpoch();
        var relevantElements = SelectRelevantElements(elements, systemContext, request);
        if (relevantElements.Count == 0)
            throw new GuideServiceException(GuideFailureKind.InvalidResponse, "前面アプリを特定できないため、UI候補を送信しません。");

        var approval = _privacyGate.ApproveStructured(request, relevantElements, history, systemContext);
        EnsureApproved(approval);

        EnsurePlanningContextCurrent(systemContext);
        EnsurePrivacyEpochCurrent(privacyEpoch);
        var decision = await SendAsync<GuideDecision>("/v2/plan", approval.Body!, cancellationToken);
        EnsurePlanningContextCurrent(systemContext);
        EnsurePrivacyEpochCurrent(privacyEpoch);
        return decision;
    }

    private async Task<GuideDecision> PlanOutlawStructuredAsync(
        string request,
        IReadOnlyList<UiElementCandidate> elements,
        IReadOnlyList<GuideHistoryItem> history,
        SystemContextSnapshot systemContext,
        CancellationToken cancellationToken)
    {
        EnsureCloudConfigured();
        var evidence = GuidanceEvidenceService.Build(false, elements, history, systemContext);
        var body = new
        {
            request,
            history,
            systemContext = new
            {
                systemContext.ForegroundProcess,
                systemContext.ForegroundTitle,
                systemContext.ForegroundProcessId,
                foregroundWindowHandle = systemContext.ForegroundWindowHandle.ToInt64(),
                systemContext.TaskbarVisible,
                runningApps = systemContext.RunningApps,
                browser = systemContext.Browser
            },
            evidence,
            elements
        };

        CloudAiResponse response;
        try
        {
            response = await PerformanceTrace.MeasureAsync(
                "cloud.outlaw-structured",
                () => _adapter.PostJsonAsync(
                    "/v1/outlaw-plan",
                    body,
                    TimeSpan.FromSeconds(60),
                    cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            throw new GuideServiceException(
                GuideFailureKind.ServiceUnavailable,
                "無法者版GLMの構造判断が60秒以内に完了しませんでした。",
                ex);
        }
        catch (HttpRequestException ex)
        {
            throw new GuideServiceException(
                GuideFailureKind.Network,
                "無法者版GLM APIへの通信に失敗しました。",
                ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            var kind = IsTransientStatus(response.StatusCode)
                ? GuideFailureKind.ServiceUnavailable
                : GuideFailureKind.Rejected;
            throw new GuideServiceException(kind, $"Outlaw API {response.StatusCode}: {Short(response.Body)}");
        }

        QualityGuideDecision quality;
        try
        {
            quality = JsonSerializer.Deserialize<QualityGuideDecision>(response.Body, _jsonOptions)
                      ?? throw new JsonException("empty response");
        }
        catch (JsonException ex)
        {
            throw new GuideServiceException(
                GuideFailureKind.InvalidResponse,
                "無法者版GLMの構造判断応答が不正です。",
                ex);
        }

        return new GuideDecision(
            quality.Status,
            quality.TargetId,
            quality.Action,
            quality.Instruction,
            quality.Question,
            quality.Key,
            quality.Confidence);
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
        var privacyEpoch = CapturePrivacyEpoch();
        var relevantElements = SelectRelevantElements(elements, systemContext, request);
        var approval = _privacyGate.ApproveVision(request, frame, relevantElements, history, systemContext);
        EnsureApproved(approval);

        EnsurePlanningContextCurrent(systemContext);
        EnsurePrivacyEpochCurrent(privacyEpoch);
        var decision = await SendAsync<VisionGuideDecision>("/v1/vision-guide", approval.Body!, cancellationToken);
        EnsurePlanningContextCurrent(systemContext);
        EnsurePrivacyEpochCurrent(privacyEpoch);
        return decision;
    }

    private long CapturePrivacyEpoch()
    {
        var provider = _privacyEpochProvider;
        if (provider is null) return 0;
        try { return provider(); }
        catch
        {
            throw new GuideServiceException(
                GuideFailureKind.ContextChanged,
                "安全状態の世代を確認できないため、外部送信を停止しました。");
        }
    }

    private void EnsurePrivacyEpochCurrent(long expected)
    {
        var provider = _privacyEpochProvider;
        if (provider is null) return;

        long current;
        try { current = provider(); }
        catch
        {
            throw new GuideServiceException(
                GuideFailureKind.ContextChanged,
                "安全状態の世代を再確認できないため、外部送信を停止しました。");
        }

        if (current != expected)
            throw new GuideServiceException(
                GuideFailureKind.ContextChanged,
                "安全確認後に画面内容が更新されたため、古い送信候補を破棄しました。");
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

        const int totalBudget = 96;
        var office = foregroundName.Equals("excel", StringComparison.OrdinalIgnoreCase) ||
                     foregroundName.Equals("winword", StringComparison.OrdinalIgnoreCase) ||
                     foregroundName.Equals("powerpnt", StringComparison.OrdinalIgnoreCase);
        var contextBudget = systemContext.Browser is not null ? 28 : office ? 32 : 20;
        var interactiveBudget = totalBudget - contextBudget;
        var goalTerms = GoalTerms(request);

        var selected = new List<UiElementCandidate>(totalBudget);
        selected.AddRange(relevant
            .Where(x => x.Interactable)
            .OrderByDescending(x => CandidateGoalPriority(x, goalTerms))
            .ThenByDescending(x => x.Focused)
            .ThenByDescending(x => x.Enabled)
            .ThenByDescending(x => !string.IsNullOrWhiteSpace(x.Name))
            .Take(interactiveBudget));

        selected.AddRange(relevant
            .Where(x => !x.Interactable && !string.IsNullOrWhiteSpace(x.Name))
            .OrderByDescending(x => ContextPriority(x) + CandidateGoalPriority(x, goalTerms))
            .Take(contextBudget));

        if (selected.Count < totalBudget)
        {
            var selectedIds = selected.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
            selected.AddRange(relevant
                .Where(x => !selectedIds.Contains(x.Id))
                .OrderByDescending(x => CandidateGoalPriority(x, goalTerms))
                .Take(totalBudget - selected.Count));
        }

        return selected.Take(totalBudget).ToArray();
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

        // Standard non-client controls live in the title band. Requiring both a canonical
        // automation identity and the top band avoids suppressing an application's own
        // content-level Close/Restore buttons.
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

    private static string[] GoalTerms(string? request)
    {
        if (string.IsNullOrWhiteSpace(request)) return [];
        return request
            .ToLowerInvariant()
            .Split([' ', '　', '\t', '\r', '\n', '、', '。', ',', '.', '/', '／'], StringSplitOptions.RemoveEmptyEntries)
            .Where(x => x.Length >= 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToArray();
    }

    private static int CandidateGoalPriority(UiElementCandidate item, IReadOnlyList<string> goalTerms)
    {
        var score = item.Focused ? 120 : 0;
        if (item.Enabled) score += 15;
        if (item.Interactable) score += 20;
        if ((item.AutomationId ?? string.Empty).Contains("role:", StringComparison.OrdinalIgnoreCase)) score += 35;

        var haystack = $"{item.Name} {item.AutomationId} {item.ClassName}".ToLowerInvariant();
        foreach (var term in goalTerms)
        {
            if (haystack.Contains(term, StringComparison.OrdinalIgnoreCase)) score += 45;
        }

        score += item.ControlType switch
        {
            "Edit" or "ComboBox" => 28,
            "Button" or "Hyperlink" or "MenuItem" => 24,
            "TabItem" or "ListItem" or "TreeItem" => 18,
            _ => 0
        };
        return score;
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

    private void EnsurePlanningContextCurrent(SystemContextSnapshot expected)
    {
        var verifier = _contextVerifier ?? throw new GuideServiceException(
            GuideFailureKind.ContextChanged,
            "現在画面の検証器が初期化されていないため、案内結果を使用しません。");
        var current = verifier.Capture();
        var foregroundChanged = expected.ForegroundProcessId <= 0 || current.ForegroundProcessId <= 0 ||
                                expected.ForegroundProcessId != current.ForegroundProcessId ||
                                !expected.ForegroundProcess.Equals(current.ForegroundProcess, StringComparison.OrdinalIgnoreCase);
        var windowChanged = expected.ForegroundWindowHandle == nint.Zero ||
                            current.ForegroundWindowHandle == nint.Zero ||
                            expected.ForegroundWindowHandle != current.ForegroundWindowHandle;

        var expectedDomain = expected.Browser?.Domain ?? string.Empty;
        var currentDomain = current.Browser?.Domain ?? string.Empty;
        var browserChanged = !string.IsNullOrWhiteSpace(expectedDomain) && !string.IsNullOrWhiteSpace(currentDomain) &&
                             !expectedDomain.Equals(currentDomain, StringComparison.OrdinalIgnoreCase);

        if (foregroundChanged || windowChanged || browserChanged)
            throw new GuideServiceException(GuideFailureKind.ContextChanged, "操作中の画面が切り替わったため、古い案内応答を破棄しました。");
    }

    private async Task<T> SendAsync<T>(
        string path,
        object body,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        EnsureCircuitAllowsRequest();
        GuideServiceException? lastTransientError = null;

        // One immediate transport retry is allowed. Higher layers must not start a second planner
        // for the same observation merely because the network had a transient failure.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var response = await PerformanceTrace.MeasureAsync(
                    CloudPhase(path),
                    () => _adapter.PostJsonAsync(path, body, timeout ?? AttemptTimeout, cancellationToken));
                if (response.IsSuccessStatusCode)
                {
                    ResetCircuit();
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

                var transient = IsTransientStatus(response.StatusCode);
                var kind = transient ? GuideFailureKind.ServiceUnavailable : GuideFailureKind.Rejected;
                var apiError = new GuideServiceException(kind, $"HelpSys API {response.StatusCode}: {Short(response.Body)}");
                if (!transient)
                {
                    ResetCircuit();
                    throw apiError;
                }

                lastTransientError = apiError;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                lastTransientError = new GuideServiceException(
                    GuideFailureKind.ServiceUnavailable,
                    "案内モデルの応答が6秒を超えました。");
            }
            catch (HttpRequestException ex)
            {
                lastTransientError = new GuideServiceException(
                    GuideFailureKind.Network,
                    "HelpSys APIへの通信に失敗しました。",
                    ex);
            }

            if (attempt == 0)
                await Task.Delay(220, cancellationToken);
        }

        RecordTransientFailure();
        throw lastTransientError ?? new GuideServiceException(GuideFailureKind.Network, "HelpSys APIへの通信に失敗しました。");
    }

    private void EnsureCircuitAllowsRequest()
    {
        lock (_circuitGate)
        {
            if (_circuitOpenUntilUtc <= DateTime.UtcNow) return;
            throw new GuideServiceException(
                GuideFailureKind.ServiceUnavailable,
                "案内サービスの一時障害が続いているため、短時間の自動再試行を停止しています。");
        }
    }

    private void RecordTransientFailure()
    {
        lock (_circuitGate)
        {
            _consecutiveTransientFailures++;
            if (_consecutiveTransientFailures < CircuitFailureThreshold) return;
            _circuitOpenUntilUtc = DateTime.UtcNow + CircuitOpenDuration;
            _consecutiveTransientFailures = 0;
        }
    }

    private void ResetCircuit()
    {
        lock (_circuitGate)
        {
            _consecutiveTransientFailures = 0;
            _circuitOpenUntilUtc = DateTime.MinValue;
        }
    }

    private static bool IsUnsupportedUnifiedRoute(GuideServiceException error)
        => error.Kind == GuideFailureKind.Rejected &&
           (error.Message.Contains("HelpSys API 404", StringComparison.OrdinalIgnoreCase) ||
            error.Message.Contains("HelpSys API 405", StringComparison.OrdinalIgnoreCase));

    private sealed record StablePlanCompatibilityResponse(
        string Status,
        string Action,
        string Instruction,
        string? Question,
        string? TargetId,
        string? Key,
        double Confidence,
        double X,
        double Y,
        double Width,
        double Height);

    private static string CloudPhase(string path)
    {
        if (path.Equals("/v2/plan", StringComparison.OrdinalIgnoreCase)) return "cloud.plan";
        if (path.Contains("quality-guide", StringComparison.OrdinalIgnoreCase)) return "cloud.quality-plan";
        if (path.Contains("vision-guide", StringComparison.OrdinalIgnoreCase)) return "cloud.vision-plan";
        if (path.Contains("/guide", StringComparison.OrdinalIgnoreCase)) return "cloud.structured-plan";
        return "cloud.request";
    }

    private static bool IsTransientStatus(int statusCode) => statusCode is 408 or 429 or 500 or 502 or 503 or 504;
    private static string Short(string value) => value.Length <= 180 ? value : value[..180];

    public void Dispose()
    {
        if (_ownsAdapter) _adapter.Dispose();
    }
}

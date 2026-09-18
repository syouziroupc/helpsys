using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace HelpSys.Stable;

internal sealed class GeminiPlannerClient : IDisposable
{
    private static readonly Uri DefaultEndpoint = new("https://helpsys.syouziroupc.workers.dev/v1/plan");
    private readonly HttpClient _http;
    private readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };

    public GeminiPlannerClient()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("HelpSys-Stable/" + VersionInfo.Version);
        _http.DefaultRequestHeaders.Add("x-helpsys-version", VersionInfo.Version);
    }

    public async Task<PlanResult> PlanAsync(
        string goal,
        ScreenObservation observation,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(goal))
            throw new PlannerException("やりたいことを入力してください。");

        var body = new
        {
            goal = goal.Trim(),
            processName = observation.ProcessName,
            windowTitle = observation.WindowTitle,
            browserDomain = observation.BrowserDomain,
            controls = observation.Controls.Select(x => new
            {
                id = x.Id,
                name = x.Name,
                automationId = x.AutomationId,
                className = x.ClassName,
                controlType = x.ControlType,
                enabled = x.Enabled,
                focused = x.Focused,
                keyboardFocusable = x.KeyboardFocusable,
                x = x.X,
                y = x.Y,
                width = x.Width,
                height = x.Height
            }).ToArray(),
            image = observation.ImageDataUri
        };

        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsJsonAsync(ResolveEndpoint(), body, _json, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            throw new PlannerException("Gemini 3.8 Flashの応答が30秒以内に完了しませんでした。操作は実行されていません。", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new PlannerException("Gemini案内サービスへ接続できませんでした。操作は実行されていません。", ex);
        }

        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            if ((int)response.StatusCode == 503 && text.Contains("gemini_unconfigured", StringComparison.OrdinalIgnoreCase))
                throw new PlannerException("Gemini APIがサーバー側で未設定です。旧モデルへは切り替えません。");
            throw new PlannerException($"Gemini案内サービスが応答できませんでした (HTTP {(int)response.StatusCode})。操作は実行されていません。");
        }

        PlanResult? plan;
        try
        {
            plan = JsonSerializer.Deserialize<PlanResult>(text, _json);
        }
        catch (JsonException ex)
        {
            throw new PlannerException("Geminiの応答形式が壊れていたため、案内を表示しませんでした。", ex);
        }

        if (plan is null) throw new PlannerException("Geminiから案内結果を受け取れませんでした。");
        Validate(plan, observation.Controls);
        return plan;
    }

    internal static void Validate(PlanResult plan, IReadOnlyList<UiControlSnapshot> controls)
    {
        if (plan.Status is not ("target" or "clarify" or "done"))
            throw new PlannerException("Geminiの案内状態が不正です。");

        if (plan.Status == "done" && plan.Confidence < 0.85)
            throw new PlannerException("完了判定の確度が不足しているため、完了扱いにしません。");

        if (SafetyGate.ContainsSecretRequest(plan.Instruction) || SafetyGate.ContainsSecretRequest(plan.Question))
            throw new PlannerException("秘密情報の入力を求める案内を検出したため、案内を表示しませんでした。");

        var allowedActions = new HashSet<string>(StringComparer.Ordinal)
        {
            "left_click", "double_click", "type_text", "press_key", "none"
        };
        if (!allowedActions.Contains(plan.Action))
            throw new PlannerException("Geminiの操作種別が不正です。");

        if (plan.Status == "target")
        {
            if (plan.Action == "none")
                throw new PlannerException("操作対象がある案内なのに操作種別がありません。");
            if (string.IsNullOrWhiteSpace(plan.Instruction))
                throw new PlannerException("Geminiの操作説明が空です。");

            UiControlSnapshot? target = null;
            if (!string.IsNullOrWhiteSpace(plan.TargetId))
            {
                target = controls.FirstOrDefault(x => x.Id == plan.TargetId);
                if (target is null || !target.Enabled)
                    throw new PlannerException("Geminiが現在操作できない対象を返したため、案内を表示しませんでした。");
            }

            if (plan.Action is "left_click" or "double_click" or "type_text")
            {
                var hasStructured = target is not null;
                var hasVisual = plan.Width > 0 && plan.Height > 0;
                if (!hasStructured && !hasVisual)
                    throw new PlannerException("Geminiが操作位置を特定できていないため、案内を表示しませんでした。");
                if (hasStructured && plan.Confidence < 0.65)
                    throw new PlannerException("操作対象の確度が低すぎるため、案内を表示しませんでした。");
                if (!hasStructured && plan.Confidence < 0.85)
                    throw new PlannerException("画像だけの操作位置の確度が不足しているため、案内を表示しませんでした。");
            }

            if (plan.Action == "type_text" && (target is null || !target.Focused || !target.KeyboardFocusable))
                throw new PlannerException("文字入力先が現在フォーカスされていないため、入力案内を表示しませんでした。");

            if (plan.Action == "press_key" && (string.IsNullOrWhiteSpace(plan.Key) || plan.Confidence < 0.72))
                throw new PlannerException("キーボード操作の根拠が不足しているため、案内を表示しませんでした。");
        }

        if (plan.Status == "clarify")
        {
            if (plan.Action != "none")
                throw new PlannerException("確認質問に操作指示が混在しているため、案内を表示しませんでした。");
            if (string.IsNullOrWhiteSpace(plan.Question))
                throw new PlannerException("Geminiの確認質問が空です。");
        }

        if (plan.Status == "done" && plan.Action != "none")
            throw new PlannerException("完了判定に操作指示が混在しているため、完了扱いにしません。");
    }

    private static Uri ResolveEndpoint()
    {
        var configured = Environment.GetEnvironmentVariable("HELPSYS_STABLE_ENDPOINT")?.Trim();
        return Uri.TryCreate(configured, UriKind.Absolute, out var uri) ? uri : DefaultEndpoint;
    }

    public void Dispose() => _http.Dispose();
}

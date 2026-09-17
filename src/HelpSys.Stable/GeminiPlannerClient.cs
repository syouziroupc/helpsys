using System.Net.Http.Json;
using System.Text.Json;

namespace HelpSys.Stable;

internal sealed class GeminiPlannerClient : IDisposable
{
    private static readonly Uri DefaultEndpoint = new("https://helpsys-stable.syouziroupc.workers.dev/v1/plan");
    private readonly HttpClient _http;
    private readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };

    public GeminiPlannerClient()
    {
        _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(28)
        };
    }

    public async Task<PlanResult> PlanAsync(
        string goal,
        ScreenObservation observation,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(goal)) throw new PlannerException("やりたいことを入力してください。");

        var endpoint = ResolveEndpoint();
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
            response = await _http.PostAsJsonAsync(endpoint, body, _json, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            throw new PlannerException("Geminiの応答が時間内に完了しませんでした。操作は実行されていません。", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new PlannerException("Gemini案内サービスへ接続できませんでした。操作は実行されていません。", ex);
        }

        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new PlannerException($"Gemini案内サービスが応答できませんでした (HTTP {(int)response.StatusCode})。操作は実行されていません。");

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

    private static void Validate(PlanResult plan, IReadOnlyList<UiControlSnapshot> controls)
    {
        if (plan.Status is not ("target" or "clarify" or "done"))
            throw new PlannerException("Geminiの案内状態が不正です。");

        if (plan.Status == "target")
        {
            if (string.IsNullOrWhiteSpace(plan.Instruction))
                throw new PlannerException("Geminiの操作説明が空です。");

            if (!string.IsNullOrWhiteSpace(plan.TargetId) && controls.All(x => x.Id != plan.TargetId))
                throw new PlannerException("Geminiが現在画面に存在しない操作対象を返したため、案内を表示しませんでした。");

            if (plan.Action is "left_click" or "double_click" or "type_text")
            {
                var hasStructured = !string.IsNullOrWhiteSpace(plan.TargetId);
                var hasVisual = plan.Width > 0 && plan.Height > 0;
                if (!hasStructured && !hasVisual)
                    throw new PlannerException("Geminiが操作位置を特定できていないため、案内を表示しませんでした。");
            }
        }

        if (plan.Status == "clarify" && string.IsNullOrWhiteSpace(plan.Question))
            throw new PlannerException("Geminiの確認質問が空です。");
    }

    private static Uri ResolveEndpoint()
    {
        var configured = Environment.GetEnvironmentVariable("HELPSYS_STABLE_ENDPOINT")?.Trim();
        return Uri.TryCreate(configured, UriKind.Absolute, out var uri) ? uri : DefaultEndpoint;
    }

    public void Dispose() => _http.Dispose();
}

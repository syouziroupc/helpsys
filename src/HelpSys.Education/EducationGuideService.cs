using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace HelpSys.Education;

public sealed class EducationGuideService : IDisposable
{
    public const string DefaultApiBase = "https://helpsys.syouziroupc.workers.dev";
    private const string CloudflareCompatibleUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/142.0.0.0 Safari/537.36";

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(25) };
    private readonly string _apiBase;
    private readonly string? _apiKey = Environment.GetEnvironmentVariable("HELPSYS_EDUCATION_API_KEY");

    public EducationGuideService()
    {
        _apiBase = (Environment.GetEnvironmentVariable("HELPSYS_EDUCATION_API_BASE") ?? DefaultApiBase).TrimEnd('/');
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", CloudflareCompatibleUserAgent);
        _http.DefaultRequestHeaders.TryAddWithoutValidation("x-helpsys-client", "education");
    }

    public bool IsConfigured => Uri.TryCreate(_apiBase, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http";

    public async Task<EducationAssistResponse> GetPracticeHintAsync(LessonDefinition lesson, int hintLevel, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured) throw new InvalidOperationException("教育AIの接続先が正しくありません。");

        Exception? lastError = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var request = CreatePracticeRequest(lesson, hintLevel);
                using var response = await _http.SendAsync(request, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadFromJsonAsync<EducationAssistResponse>(cancellationToken: cancellationToken);
                    return body is { Message.Length: > 0 }
                        ? body
                        : throw new InvalidOperationException("教育AIから有効な説明を受け取れませんでした。");
                }

                var error = new InvalidOperationException($"教育AIが応答できませんでした ({(int)response.StatusCode})。");
                if (!IsTransient(response.StatusCode) || attempt > 0) throw error;
                lastError = error;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (HttpRequestException ex)
            {
                if (attempt > 0) throw new InvalidOperationException("教育AIへの通信に失敗しました。", ex);
                lastError = ex;
            }

            await Task.Delay(350, cancellationToken);
        }

        throw new InvalidOperationException("教育AIへの通信に失敗しました。", lastError);
    }

    private HttpRequestMessage CreatePracticeRequest(LessonDefinition lesson, int hintLevel)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{_apiBase}/v1/education/assist")
        {
            Content = JsonContent.Create(new
            {
                stage = "practice",
                lessonId = lesson.Id,
                lessonTitle = lesson.Title,
                objective = lesson.PracticeText,
                message = "この練習で次に考えることのヒントをください。",
                hintLevel = Math.Clamp(hintLevel, 1, 3)
            })
        };
        if (!string.IsNullOrWhiteSpace(_apiKey)) request.Headers.TryAddWithoutValidation("x-helpsys-education-key", _apiKey);
        return request;
    }

    private static bool IsTransient(HttpStatusCode statusCode) => (int)statusCode is 408 or 429 or 500 or 502 or 503 or 504;

    public void Dispose() => _http.Dispose();
}

public sealed record EducationAssistResponse(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("nextHintLevel")] int? NextHintLevel);
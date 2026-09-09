using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace HelpSys.Education;

public sealed class EducationGuideService
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(25) };
    private readonly string? _apiBase = Environment.GetEnvironmentVariable("HELPSYS_EDUCATION_API_BASE")?.TrimEnd('/');
    private readonly string? _apiKey = Environment.GetEnvironmentVariable("HELPSYS_EDUCATION_API_KEY");

    public bool IsConfigured => Uri.TryCreate(_apiBase, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http";

    public async Task<EducationAssistResponse> GetPracticeHintAsync(LessonDefinition lesson, int hintLevel, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured) throw new InvalidOperationException("教育AIの接続先が設定されていません。");

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_apiBase}/v1/education/assist")
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
        if (!string.IsNullOrWhiteSpace(_apiKey)) request.Headers.Add("x-helpsys-education-key", _apiKey);

        using var response = await _http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"教育AIが応答できませんでした ({(int)response.StatusCode})。");
        var body = await response.Content.ReadFromJsonAsync<EducationAssistResponse>(cancellationToken: cancellationToken);
        return body is { Message.Length: > 0 } ? body : throw new InvalidOperationException("教育AIから有効な説明を受け取れませんでした。");
    }
}

public sealed record EducationAssistResponse(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("nextHintLevel")] int? NextHintLevel);

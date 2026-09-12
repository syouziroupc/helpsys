using System.Net;
using System.Net.Http;
using System.Text.Json;
using HelpSys.Shared;

namespace HelpSys.Education;

public sealed class EducationGuideService : IDisposable
{
    public const string DefaultApiBase = "https://helpsys.syouziroupc.workers.dev";

    private readonly CloudAiAdapter _adapter;
    private readonly string _apiBase;
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

    public EducationGuideService()
    {
        _apiBase = (Environment.GetEnvironmentVariable("HELPSYS_EDUCATION_API_BASE") ?? DefaultApiBase).TrimEnd('/');
        _adapter = new CloudAiAdapter(
            _apiBase,
            Environment.GetEnvironmentVariable("HELPSYS_EDUCATION_API_KEY"),
            "x-helpsys-education-key");
    }

    public bool IsConfigured => Uri.TryCreate(_apiBase, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http";

    public async Task<EducationAssistResponse> GetPracticeHintAsync(
        LessonDefinition lesson,
        int hintLevel,
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured) throw new InvalidOperationException("教育AIの接続先が正しくありません。");

        Exception? lastError = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var response = await _adapter.PostJsonAsync(
                    "/v1/education/assist",
                    new
                    {
                        stage = "practice",
                        lessonId = lesson.Id,
                        lessonTitle = lesson.Title,
                        objective = lesson.PracticeText,
                        message = "この練習で次に考えることのヒントをください。",
                        hintLevel = Math.Clamp(hintLevel, 1, 3)
                    },
                    TimeSpan.FromSeconds(25),
                    cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    var body = JsonSerializer.Deserialize<EducationAssistResponse>(response.Body, _jsonOptions);
                    return body is { Message.Length: > 0 }
                        ? body
                        : throw new InvalidOperationException("教育AIから有効な説明を受け取れませんでした。");
                }

                var error = new InvalidOperationException($"教育AIが応答できませんでした ({response.StatusCode})。");
                if (!IsTransient((HttpStatusCode)response.StatusCode) || attempt > 0) throw error;
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

    private static bool IsTransient(HttpStatusCode statusCode) => (int)statusCode is 408 or 429 or 500 or 502 or 503 or 504;

    public void Dispose() => _adapter.Dispose();
}

public sealed record EducationAssistResponse(
    string Status,
    string Message,
    int? NextHintLevel);

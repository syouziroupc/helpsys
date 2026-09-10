using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace HelpSys.Shared;

public sealed record CloudAiResponse(int StatusCode, string Body)
{
    public bool IsSuccessStatusCode => StatusCode is >= 200 and <= 299;
}

/// <summary>
/// The only outbound HTTP transport used by HelpSys AI features.
/// Screen data must be approved by PrivacyGate before it reaches this adapter.
/// External endpoints must use HTTPS. Plain HTTP is accepted only for loopback development.
/// </summary>
public sealed class CloudAiAdapter : IDisposable
{
    public const string DefaultApiBase = "https://helpsys.syouziroupc.workers.dev";

    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly string _apiBase;
    private readonly string? _apiKey;
    private readonly string _apiKeyHeader;
    private bool _disposed;

    public CloudAiAdapter(
        string? apiBase = null,
        string? apiKey = null,
        string apiKeyHeader = "x-helpsys-key")
    {
        var resolvedBase = (apiBase ?? Environment.GetEnvironmentVariable("HELPSYS_API_BASE") ?? DefaultApiBase).TrimEnd('/');
        _apiBase = ValidateApiBase(resolvedBase);
        _apiKey = apiKey ?? Environment.GetEnvironmentVariable("HELPSYS_API_KEY");
        _apiKeyHeader = apiKeyHeader;
    }

    public Task<CloudAiResponse> PostJsonAsync(
        string path,
        object body,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        return SendAsync(path, JsonContent.Create(body), timeout, cancellationToken);
    }

    public Task<CloudAiResponse> PostBytesAsync(
        string path,
        byte[] body,
        string mediaType,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        return SendAsync(path, content, timeout, cancellationToken);
    }

    private async Task<CloudAiResponse> SendAsync(
        string path,
        HttpContent content,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidatePath(path);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_apiBase}{path}")
        {
            Content = content
        };
        request.Headers.TryAddWithoutValidation("x-helpsys-request-id", Guid.NewGuid().ToString("N"));
        if (!string.IsNullOrWhiteSpace(_apiKey))
            request.Headers.TryAddWithoutValidation(_apiKeyHeader, _apiKey);

        using var response = await _http.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
        return new CloudAiResponse((int)response.StatusCode, responseBody);
    }

    private static string ValidateApiBase(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            throw new InvalidOperationException("HelpSys AI APIの接続先が不正です。");

        var secureExternal = uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        var loopbackDevelopment = uri.IsLoopback && uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase);
        if (!secureExternal && !loopbackDevelopment)
            throw new InvalidOperationException("外部AI APIへの接続はHTTPSのみ許可されています。");

        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException("AI APIのベースURLに認証情報・クエリ・フラグメントを含めることはできません。");

        return value;
    }

    private static void ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !path.StartsWith("/", StringComparison.Ordinal))
            throw new ArgumentException("Cloud path must start with '/'.", nameof(path));
        if (path.Contains("?", StringComparison.Ordinal) || path.Contains("#", StringComparison.Ordinal) || path.Contains("://", StringComparison.Ordinal))
            throw new ArgumentException("Cloud path must not contain query parameters, fragments or absolute URLs.", nameof(path));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _http.Dispose();
    }
}

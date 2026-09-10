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
        _apiBase = (apiBase ?? Environment.GetEnvironmentVariable("HELPSYS_API_BASE") ?? DefaultApiBase).TrimEnd('/');
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
        if (!path.StartsWith("/", StringComparison.Ordinal)) throw new ArgumentException("Cloud path must start with '/'.", nameof(path));

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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _http.Dispose();
    }
}

using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;

namespace HelpSys.Shared;

public sealed record CloudAiResponse(int StatusCode, string Body)
{
    public bool IsSuccessStatusCode => StatusCode is >= 200 and <= 299;
}

/// <summary>
/// The only outbound HTTP transport used by HelpSys AI features.
/// Screen data must be approved by PrivacyGate before it reaches this adapter.
/// Production traffic is pinned to the reviewed HelpSys Workers origin.
/// Loopback is compiled in only for CI/local test builds, never for distributed HelpSys.
/// Redirects, cookies and OS/user proxy inheritance are disabled.
/// </summary>
public sealed class CloudAiAdapter : IDisposable
{
    public const string DefaultApiBase = "https://helpsys.syouziroupc.workers.dev";
    private const int MaxResponseBodyBytes = 1024 * 1024;

    private static readonly HashSet<string> AllowedApiKeyHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "x-helpsys-key",
        "x-helpsys-education-key"
    };

    private readonly HttpClient _http = new(CreateHandler())
    {
        Timeout = Timeout.InfiniteTimeSpan
    };
    private readonly string _apiBase;
    private readonly string? _apiKey;
    private readonly string _apiKeyHeader;
    private bool _disposed;

    public CloudAiAdapter(
        string? apiBase = null,
        string? apiKey = null,
        string apiKeyHeader = "x-helpsys-key")
    {
        if (!AllowedApiKeyHeaders.Contains(apiKeyHeader))
            throw new ArgumentException("HelpSysで許可されていないAPIキーヘッダーです。", nameof(apiKeyHeader));

        var resolvedBase = (apiBase ?? Environment.GetEnvironmentVariable("HELPSYS_API_BASE") ?? DefaultApiBase).TrimEnd('/');
        _apiBase = ValidateUnifiedApiBase(resolvedBase);
        _apiKey = apiKey ?? Environment.GetEnvironmentVariable("HELPSYS_API_KEY");
        _apiKeyHeader = apiKeyHeader;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_apiBase);

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
        request.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true, NoCache = true };
        request.Headers.Pragma.ParseAdd("no-cache");
        if (!string.IsNullOrWhiteSpace(_apiKey))
            request.Headers.TryAddWithoutValidation(_apiKeyHeader, _apiKey);

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token).ConfigureAwait(false);
        var responseBody = await ReadBoundedResponseBodyAsync(response.Content, timeoutCts.Token).ConfigureAwait(false);
        return new CloudAiResponse((int)response.StatusCode, responseBody);
    }

    private static async Task<string> ReadBoundedResponseBodyAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > MaxResponseBodyBytes)
            throw new InvalidOperationException("AI APIの応答が安全上限を超えているため処理を中止しました。");

        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream(Math.Min(MaxResponseBodyBytes, 64 * 1024));
        var chunk = new byte[16 * 1024];
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(chunk.AsMemory(0, chunk.Length), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                if (buffer.Length + read > MaxResponseBodyBytes)
                    throw new InvalidOperationException("AI APIの応答が安全上限を超えているため処理を中止しました。");
                buffer.Write(chunk, 0, read);
            }

            var backing = buffer.GetBuffer();
            return Encoding.UTF8.GetString(backing, 0, checked((int)buffer.Length));
        }
        finally
        {
            Array.Clear(chunk, 0, chunk.Length);
            if (buffer.TryGetBuffer(out var segment) && segment.Array is not null)
                Array.Clear(segment.Array, segment.Offset, segment.Count);
        }
    }

    private static HttpClientHandler CreateHandler()
    {
        return new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            CheckCertificateRevocationList = true
        };
    }

    private static string ValidateUnifiedApiBase(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            throw new InvalidOperationException("HelpSys AI APIの接続先が不正です。");

        if (uri.IsLoopback)
        {
#if HELPSYS_TEST_BUILD
            if (!uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("HelpSysのローカル試験接続先はHTTP/HTTPSのみ許可されています。");
            if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
                throw new InvalidOperationException("AI APIのベースURLに認証情報・クエリ・フラグメントを含めることはできません。");
            return value;
#else
            throw new InvalidOperationException("配布版HelpSysではloopback AI接続先を許可しません。");
#endif
        }

        if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("外部AI APIへの接続はHTTPSのみ許可されています。");
        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException("AI APIのベースURLに認証情報・クエリ・フラグメントを含めることはできません。");

        var approved = new Uri(DefaultApiBase, UriKind.Absolute);
        var sameApprovedOrigin =
            uri.Scheme.Equals(approved.Scheme, StringComparison.OrdinalIgnoreCase) &&
            uri.Host.Equals(approved.Host, StringComparison.OrdinalIgnoreCase) &&
            uri.Port == approved.Port &&
            (uri.AbsolutePath.Length == 0 || uri.AbsolutePath == "/");
        if (!sameApprovedOrigin)
            throw new InvalidOperationException("HelpSysはコードで承認されたAI API接続先以外へ送信できません。");

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

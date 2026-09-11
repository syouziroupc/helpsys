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
/// External endpoints must use HTTPS. Plain HTTP loopback is accepted only in explicit test builds.
/// Safe builds have no production cloud default and only accept the explicitly reviewed origin.
/// Redirects and cookies are disabled so approved requests cannot be silently rerouted or persisted.
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
    private readonly string? _apiBase;
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

#if HELPSYS_SAFE_BUILD
        var resolvedBase = apiBase ?? Environment.GetEnvironmentVariable("HELPSYS_SAFE_API_BASE");
        _apiBase = string.IsNullOrWhiteSpace(resolvedBase) ? null : ValidateSafeApiBase(resolvedBase.TrimEnd('/'));
        _apiKey = apiKey ?? Environment.GetEnvironmentVariable("HELPSYS_SAFE_API_KEY");
#else
        var resolvedBase = (apiBase ?? Environment.GetEnvironmentVariable("HELPSYS_API_BASE") ?? DefaultApiBase).TrimEnd('/');
        _apiBase = ValidateApiBase(resolvedBase);
        _apiKey = apiKey ?? Environment.GetEnvironmentVariable("HELPSYS_API_KEY");
#endif
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
        if (!IsConfigured)
            throw new InvalidOperationException("安全版の承認済みAI API接続先が設定されていないため、外部送信を拒否しました。");
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
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            CheckCertificateRevocationList = true
        };
#if HELPSYS_SAFE_BUILD
        // Safe must not inherit an OS/user proxy that could become an unreviewed intermediary.
        handler.UseProxy = false;
#endif
        return handler;
    }

    private static string ValidateSafeApiBase(string value)
    {
        var validated = ValidateApiBase(value);
        if (!Uri.TryCreate(validated, UriKind.Absolute, out var uri))
            throw new InvalidOperationException("安全版AI APIの接続先が不正です。");

        if (uri.IsLoopback)
        {
#if HELPSYS_SAFE_TEST_BUILD
            return validated;
#else
            throw new InvalidOperationException("安全版本番ビルドではloopback AI接続先を許可しません。");
#endif
        }

        var approved = new Uri(DefaultApiBase, UriKind.Absolute);
        var sameApprovedOrigin =
            uri.Scheme.Equals(approved.Scheme, StringComparison.OrdinalIgnoreCase) &&
            uri.Host.Equals(approved.Host, StringComparison.OrdinalIgnoreCase) &&
            uri.Port == approved.Port &&
            (uri.AbsolutePath.Length == 0 || uri.AbsolutePath == "/");

        if (!sameApprovedOrigin)
            throw new InvalidOperationException("安全版はコードで承認されたAI API接続先以外へ送信できません。");

        return validated;
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
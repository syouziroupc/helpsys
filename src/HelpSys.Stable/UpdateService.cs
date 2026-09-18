using System.Net.Http;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HelpSys.Stable;

internal sealed partial class UpdateService : IDisposable
{
    private static readonly Uri ReleaseApi = new("https://api.github.com/repos/syouziroupc/helpsys/releases/tags/preview-latest");
    private const long MaximumPackageBytes = 180L * 1024 * 1024;
    private const long MaximumExtractedBytes = 450L * 1024 * 1024;
    private const int MaximumArchiveEntries = 1200;
    private const int MaximumRedirects = 5;
    private const int MaximumMetadataBytes = 2 * 1024 * 1024;
    private const int MaximumDigestBytes = 4096;
    private static readonly HashSet<string> AllowedDownloadHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "github.com",
        "release-assets.githubusercontent.com",
        "objects.githubusercontent.com"
    };
    private readonly HttpClient _http;

    public UpdateService()
    {
        _http = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            CheckCertificateRevocationList = true
        }, disposeHandler: true) { Timeout = TimeSpan.FromMinutes(3) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("HelpSys-Stable/" + VersionInfo.Version);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    public async Task<UpdateInfo?> CheckAsync(CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(ResolveReleaseApi(), HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new PlannerException($"更新情報を取得できませんでした (HTTP {(int)response.StatusCode})。");

        var metadata = await ReadBoundedBytesAsync(response.Content, MaximumMetadataBytes, cancellationToken);
        using var document = JsonDocument.Parse(metadata);
        if (!document.RootElement.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            throw new PlannerException("更新情報の形式が不正です。");

        System.Version? newest = null;
        string? newestBuildId = null;
        Uri? stableZip = null;
        long stableZipSize = -1;
        Uri? shaUrl = null;

        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? "" : "";
            var url = asset.TryGetProperty("browser_download_url", out var urlProp) ? urlProp.GetString() ?? "" : "";
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !IsTrustedReleaseUri(uri)) continue;

            if (name.Equals("HelpSys-latest-win-x64.zip", StringComparison.OrdinalIgnoreCase))
            {
                stableZip = uri;
                stableZipSize = asset.TryGetProperty("size", out var sizeProp) && sizeProp.TryGetInt64(out var parsedSize)
                    ? parsedSize
                    : -1;
            }
            else if (name.Equals("HelpSys-latest-win-x64.sha256", StringComparison.OrdinalIgnoreCase))
                shaUrl = uri;
            else
            {
                var match = VersionedAssetRegex().Match(name);
                if (match.Success && System.Version.TryParse(match.Groups["version"].Value, out var parsed) &&
                    (newest is null || parsed > newest))
                {
                    newest = parsed;
                    newestBuildId = match.Groups["build"].Value.ToLowerInvariant();
                }
            }
        }

        if (newest is null || newestBuildId is null) return null;
        if (newest < VersionInfo.SemanticVersion) return null;

        var sameVersionAndBuild =
            newest == VersionInfo.SemanticVersion &&
            newestBuildId.Equals(VersionInfo.BuildId, StringComparison.OrdinalIgnoreCase);
        if (sameVersionAndBuild) return null;

        if (stableZip is null || shaUrl is null || stableZipSize <= 0 || stableZipSize > MaximumPackageBytes)
            throw new PlannerException("最新版はありますが、安全条件を満たす検証付き更新ファイルが揃っていません。");

        return new UpdateInfo(newest, newestBuildId, stableZip, shaUrl, stableZipSize);
    }

    public async Task InstallAsync(UpdateInfo info, CancellationToken cancellationToken)
    {
        var appDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        EnsureInstallDirectoryWritable(appDirectory);

        var root = Path.Combine(Path.GetTempPath(), "HelpSys-Stable-Update-" + Guid.NewGuid().ToString("N"));
        var zip = Path.Combine(root, "update.zip");
        var extracted = Path.Combine(root, "payload");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(extracted);

        try
        {
            if (info.SizeBytes <= 0 || info.SizeBytes > MaximumPackageBytes)
                throw new PlannerException("更新ファイルの公開サイズが安全条件を満たしません。");

            await DownloadAsync(info.ZipUrl, zip, info.SizeBytes, cancellationToken);
            var digestBytes = await DownloadSmallAsync(info.Sha256Url, MaximumDigestBytes, cancellationToken);
            var expected = System.Text.Encoding.ASCII.GetString(digestBytes)
                .Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(expected) || expected.Length != 64 || expected.Any(ch => !Uri.IsHexDigit(ch)))
                throw new PlannerException("更新ファイルのSHA-256情報が不正です。");

            await using (var input = File.OpenRead(zip))
            {
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken)).ToLowerInvariant();
                if (!CryptographicOperations.FixedTimeEquals(
                        System.Text.Encoding.ASCII.GetBytes(actual),
                        System.Text.Encoding.ASCII.GetBytes(expected)))
                    throw new PlannerException("更新ファイルのSHA-256検証に失敗しました。更新しません。");
            }

            ExtractVerifiedPackage(zip, extracted);
            var newExe = Path.Combine(extracted, "HelpSys.Stable.exe");
            if (!File.Exists(newExe))
                throw new PlannerException("更新パッケージにHelpSys本体がありません。");

            var script = Path.Combine(root, "apply-update.ps1");
            var log = Path.Combine(root, "update.log");
            var currentPid = Environment.ProcessId;

            var escapedExtracted = EscapePowerShell(extracted);
            var escapedDirectory = EscapePowerShell(appDirectory);
            var escapedExe = EscapePowerShell(Path.Combine(appDirectory, "HelpSys.Stable.exe"));
            var escapedLog = EscapePowerShell(log);
            var scriptText = string.Join(Environment.NewLine,
            [
                "$ErrorActionPreference = 'Stop'",
                "try {",
                $"  Wait-Process -Id {currentPid} -ErrorAction SilentlyContinue",
                "  Start-Sleep -Milliseconds 500",
                $"  Copy-Item -Path '{escapedExtracted}\\*' -Destination '{escapedDirectory}' -Recurse -Force",
                $"  Start-Process -FilePath '{escapedExe}'",
                "} catch {",
                $"  $_ | Out-String | Set-Content -Encoding UTF8 '{escapedLog}'",
                $"  if (Test-Path '{escapedExe}') {{ try {{ Start-Process -FilePath '{escapedExe}' }} catch {{ }} }}",
                "}"
            ]) + Environment.NewLine;
            await File.WriteAllTextAsync(script, scriptText, cancellationToken);

            _ = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoLogo -NoProfile -ExecutionPolicy Bypass -File \"{script}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = root
            }) ?? throw new PlannerException("更新適用プロセスを開始できませんでした。");
        }
        catch
        {
            try { Directory.Delete(root, recursive: true); } catch { }
            throw;
        }
    }

    private async Task DownloadAsync(Uri uri, string destination, long expectedSize, CancellationToken cancellationToken)
    {
        using var response = await SendWithAllowedRedirectsAsync(uri, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new PlannerException($"更新ファイルを取得できませんでした (HTTP {(int)response.StatusCode})。");
        if (response.Content.Headers.ContentLength is long declared &&
            (declared <= 0 || declared > MaximumPackageBytes || declared != expectedSize))
            throw new PlannerException("更新ファイルのサイズが公開情報と一致しません。");

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var target = new FileStream(
            destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0) break;
            total += read;
            if (total > expectedSize || total > MaximumPackageBytes)
                throw new PlannerException("更新ファイルが公開サイズを超えました。");
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        if (total != expectedSize)
            throw new PlannerException("更新ファイルのサイズが公開情報と一致しません。");
    }

    private async Task<byte[]> DownloadSmallAsync(Uri uri, int maxBytes, CancellationToken cancellationToken)
    {
        using var response = await SendWithAllowedRedirectsAsync(uri, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new PlannerException($"更新検証情報を取得できませんでした (HTTP {(int)response.StatusCode})。");
        return await ReadBoundedBytesAsync(response.Content, maxBytes, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendWithAllowedRedirectsAsync(Uri initial, CancellationToken cancellationToken)
    {
        var current = initial;
        for (var redirect = 0; redirect <= MaximumRedirects; redirect++)
        {
            if (!IsAllowedDownloadUri(current))
                throw new PlannerException("更新ファイルが承認されていないホストへ移動しようとしました。");

            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!IsRedirect(response.StatusCode)) return response;

            var location = response.Headers.Location;
            response.Dispose();
            if (location is null)
                throw new PlannerException("更新ファイルのリダイレクト先が空です。");
            current = location.IsAbsoluteUri ? location : new Uri(current, location);
        }
        throw new PlannerException("更新ファイルのリダイレクト回数が上限を超えました。");
    }

    private static bool IsRedirect(HttpStatusCode status)
        => status is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod or
           HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private static bool IsAllowedDownloadUri(Uri uri)
        => uri.Scheme == Uri.UriSchemeHttps &&
           uri.IsDefaultPort &&
           AllowedDownloadHosts.Contains(uri.Host);

    private static async Task<byte[]> ReadBoundedBytesAsync(HttpContent content, int maxBytes, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is long declared && (declared < 0 || declared > maxBytes))
            throw new PlannerException("更新サービスの応答が安全上限を超えました。");
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream(Math.Min(maxBytes, 64 * 1024));
        var chunk = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(chunk.AsMemory(), cancellationToken);
            if (read == 0) break;
            if (buffer.Length + read > maxBytes)
                throw new PlannerException("更新サービスの応答が安全上限を超えました。");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private static void ExtractVerifiedPackage(string zipPath, string destination)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        if (archive.Entries.Count > MaximumArchiveEntries)
            throw new PlannerException("更新パッケージのファイル数が安全上限を超えています。");

        var declaredExtractedBytes = archive.Entries.Sum(entry => entry.Length);
        if (declaredExtractedBytes <= 0 || declaredExtractedBytes > MaximumExtractedBytes)
            throw new PlannerException("更新パッケージの展開サイズが安全上限を超えています。");

        var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        foreach (var entry in archive.Entries)
        {
            var target = Path.GetFullPath(Path.Combine(destination, entry.FullName));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new PlannerException("更新パッケージに不正なパスが含まれています。");
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(target);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
    }

    private static void EnsureInstallDirectoryWritable(string installDirectory)
    {
        var probe = Path.Combine(installDirectory, ".helpsys-update-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(probe, "probe");
        }
        catch (Exception ex)
        {
            throw new PlannerException(
                "現在のHelpSysフォルダーへ更新を書き込めません。書き込み可能なフォルダーへ移してから更新してください。",
                ex);
        }
        finally
        {
            try { if (File.Exists(probe)) File.Delete(probe); } catch { }
        }
    }

    private static string EscapePowerShell(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private static Uri ResolveReleaseApi()
    {
        var configured = Environment.GetEnvironmentVariable("HELPSYS_UPDATE_API")?.Trim();
        if (Uri.TryCreate(configured, UriKind.Absolute, out var uri) &&
            uri.IsLoopback &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            return uri;
        return ReleaseApi;
    }

    internal static bool IsTrustedReleaseUri(Uri uri)
        => uri.Scheme == Uri.UriSchemeHttps &&
           uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
           uri.AbsolutePath.StartsWith(
               "/syouziroupc/helpsys/releases/download/preview-latest/",
               StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"^HelpSys-Stable-(?<version>\d+\.\d+\.\d+)-(?<build>[0-9a-fA-F]{8})-win-x64\.zip$",
        RegexOptions.CultureInvariant)]
    private static partial Regex VersionedAssetRegex();

    public void Dispose() => _http.Dispose();
}

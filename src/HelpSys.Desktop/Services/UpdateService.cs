using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HelpSys.Services;

public sealed record UpdateInfo(
    string BuildId,
    string AssetName,
    Uri DownloadUri,
    string Sha256,
    long SizeBytes);

public sealed record PreparedUpdate(
    UpdateInfo Info,
    string ScriptPath);

/// <summary>
/// Update transport is deliberately separate from AI/cloud transport: it sends no user content and
/// can only read the fixed HelpSys GitHub release endpoint. Packages are accepted only when the
/// release supplies a SHA-256 digest and the downloaded bytes match it exactly.
/// </summary>
public sealed class UpdateService : IDisposable
{
    private const string LatestReleaseApi = "https://api.github.com/repos/syouziroupc/helpsys/releases/tags/outlaw-latest";
    private const string RequiredTag = "outlaw-latest";
    private const long MaximumPackageBytes = 180L * 1024 * 1024;
    private const int MaximumRedirects = 5;

    private static readonly Regex VersionedAssetRegex = new(
        @"^HelpSys-Outlaw-(?:\d+\.\d+\.\d+-)?(?<build>[0-9a-f]{8})-win-x64\.zip$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly HashSet<string> AllowedDownloadHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "github.com",
        "release-assets.githubusercontent.com",
        "objects.githubusercontent.com"
    };

    private readonly HttpClient _http;

    public UpdateService()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            CheckCertificateRevocationList = true
        };
        _http = new HttpClient(handler, disposeHandler: true)
        {
            // The self-contained package is roughly 80 MB. A short AI-request timeout is not
            // appropriate here; package size and SHA-256 are independently bounded/verified.
            Timeout = TimeSpan.FromMinutes(3)
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("HelpSys-Updater/1.0");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    public string CurrentBuildId => GetCurrentBuildId();

    public async Task<UpdateInfo?> CheckAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"更新情報を確認できませんでした (HTTP {(int)response.StatusCode})。");

        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;

        var tag = root.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() : null;
        if (!string.Equals(tag, RequiredTag, StringComparison.Ordinal))
            throw new InvalidOperationException("HelpSysの承認済み更新チャネルを確認できませんでした。");

        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("更新ファイル一覧を確認できませんでした。");

        UpdateInfo? latest = null;
        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(name)) continue;
            var match = VersionedAssetRegex.Match(name);
            if (!match.Success) continue;

            var url = asset.TryGetProperty("browser_download_url", out var urlElement) ? urlElement.GetString() : null;
            var digest = asset.TryGetProperty("digest", out var digestElement) ? digestElement.GetString() : null;
            var size = asset.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var parsedSize)
                ? parsedSize
                : -1;

            if (!Uri.TryCreate(url, UriKind.Absolute, out var downloadUri) || !IsAllowedDownloadUri(downloadUri))
                continue;
            if (!TryParseSha256(digest, out var sha256)) continue;
            if (size <= 0 || size > MaximumPackageBytes) continue;

            latest = new UpdateInfo(
                match.Groups["build"].Value.ToLowerInvariant(),
                name,
                downloadUri,
                sha256,
                size);
            break;
        }

        if (latest is null)
            throw new InvalidOperationException("SHA-256付きHelpSys更新ファイルが見つかりませんでした。");

        return string.Equals(latest.BuildId, CurrentBuildId, StringComparison.OrdinalIgnoreCase)
            ? null
            : latest;
    }

    public async Task<PreparedUpdate> PrepareAsync(UpdateInfo update, CancellationToken cancellationToken = default)
    {
        if (!IsAllowedDownloadUri(update.DownloadUri) || update.SizeBytes <= 0 || update.SizeBytes > MaximumPackageBytes)
            throw new InvalidOperationException("更新ファイルの取得先またはサイズが安全条件を満たしません。");

        var installDirectory = Path.GetFullPath(AppContext.BaseDirectory);
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
            throw new InvalidOperationException("現在のHelpSys実行ファイルを確認できません。");

        EnsureInstallDirectoryWritable(installDirectory);

        var updateRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HelpSys",
            "Updates",
            $"{update.BuildId}-{Guid.NewGuid():N}");
        var extractDirectory = Path.Combine(updateRoot, "payload");
        Directory.CreateDirectory(extractDirectory);
        var zipPath = Path.Combine(updateRoot, "package.zip");

        try
        {
            await DownloadPackageAsync(update, zipPath, cancellationToken);
            await VerifyDigestAsync(zipPath, update.Sha256, cancellationToken);
            ExtractVerifiedPackage(zipPath, extractDirectory);

            var stagedExe = Path.Combine(extractDirectory, "HelpSys.exe");
            if (!File.Exists(stagedExe))
                throw new InvalidOperationException("更新パッケージにHelpSys.exeが含まれていません。");

            var scriptPath = CreateReplacementScript(
                updateRoot,
                extractDirectory,
                installDirectory,
                Path.GetFullPath(executablePath),
                Environment.ProcessId);

            return new PreparedUpdate(update, scriptPath);
        }
        catch
        {
            TryDeleteDirectory(updateRoot);
            throw;
        }
    }

    public static void LaunchPreparedUpdate(PreparedUpdate prepared)
    {
        if (string.IsNullOrWhiteSpace(prepared.ScriptPath) || !File.Exists(prepared.ScriptPath))
            throw new InvalidOperationException("更新処理ファイルを確認できません。");

        var start = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(prepared.ScriptPath) ?? Path.GetTempPath()
        };
        start.ArgumentList.Add("/d");
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add(prepared.ScriptPath);
        if (Process.Start(start) is null)
            throw new InvalidOperationException("更新処理を開始できませんでした。");
    }

    private async Task DownloadPackageAsync(UpdateInfo update, string destination, CancellationToken cancellationToken)
    {
        var current = update.DownloadUri;
        for (var redirect = 0; redirect <= MaximumRedirects; redirect++)
        {
            if (!IsAllowedDownloadUri(current))
                throw new InvalidOperationException("更新ファイルが承認されていないホストへ移動しようとしました。");

            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (IsRedirect(response.StatusCode))
            {
                var location = response.Headers.Location
                    ?? throw new InvalidOperationException("更新ファイルのリダイレクト先が空です。");
                current = location.IsAbsoluteUri ? location : new Uri(current, location);
                continue;
            }

            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"更新ファイルを取得できませんでした (HTTP {(int)response.StatusCode})。");

            if (response.Content.Headers.ContentLength is long contentLength &&
                (contentLength <= 0 || contentLength > MaximumPackageBytes || contentLength != update.SizeBytes))
                throw new InvalidOperationException("更新ファイルのサイズが公開情報と一致しません。");

            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var output = new FileStream(
                destination,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            var buffer = new byte[81920];
            long total = 0;
            while (true)
            {
                var read = await input.ReadAsync(buffer, cancellationToken);
                if (read == 0) break;
                total += read;
                if (total > MaximumPackageBytes || total > update.SizeBytes)
                    throw new InvalidOperationException("更新ファイルが公開サイズを超えました。");
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }

            if (total != update.SizeBytes)
                throw new InvalidOperationException("更新ファイルのサイズが公開情報と一致しません。");
            return;
        }

        throw new InvalidOperationException("更新ファイルのリダイレクト回数が上限を超えました。");
    }

    private static async Task VerifyDigestAsync(string path, string expectedSha256, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, cancellationToken);
        var actual = Convert.ToHexString(hash).ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(actual),
                Encoding.ASCII.GetBytes(expectedSha256.ToLowerInvariant())))
            throw new InvalidOperationException("更新ファイルのSHA-256がGitHub公開値と一致しません。更新を中止しました。");
    }

    private static void ExtractVerifiedPackage(string zipPath, string destination)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var destinationRoot = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        foreach (var entry in archive.Entries)
        {
            var target = Path.GetFullPath(Path.Combine(destination, entry.FullName));
            if (!target.StartsWith(destinationRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("更新パッケージに不正なパスが含まれています。");
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
    }

    private static string CreateReplacementScript(
        string updateRoot,
        string payloadDirectory,
        string installDirectory,
        string executablePath,
        int processId)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"HelpSys-Update-{Guid.NewGuid():N}.cmd");
        var lines = new[]
        {
            "@echo off",
            // HelpSys paths can contain Japanese user/folder names. Switch cmd to UTF-8 before
            // parsing the path-bearing lines written below.
            "chcp 65001 >nul",
            "setlocal",
            ":wait_for_helpsys",
            $"tasklist /FI \"PID eq {processId}\" /NH | findstr /R /C:\"[ ]{processId}[ ]\" >nul",
            "if not errorlevel 1 (",
            "  timeout /t 1 /nobreak >nul",
            "  goto wait_for_helpsys",
            ")",
            $"xcopy /E /I /Y /Q \"{EscapeBatchPath(payloadDirectory)}\\*\" \"{EscapeBatchPath(installDirectory)}\" >nul",
            "if errorlevel 1 exit /b 1",
            $"start \"\" \"{EscapeBatchPath(executablePath)}\"",
            $"rmdir /S /Q \"{EscapeBatchPath(updateRoot)}\"",
            "del \"%~f0\""
        };
        File.WriteAllLines(scriptPath, lines, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return scriptPath;
    }

    private static void EnsureInstallDirectoryWritable(string installDirectory)
    {
        var probe = Path.Combine(installDirectory, $".helpsys-update-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(probe, "probe");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "現在のHelpSysフォルダーへ更新を書き込めません。書き込み可能なフォルダーへHelpSysを移してから更新してください。",
                ex);
        }
        finally
        {
            try { if (File.Exists(probe)) File.Delete(probe); } catch { }
        }
    }

    private static bool TryParseSha256(string? digest, out string sha256)
    {
        sha256 = string.Empty;
        if (string.IsNullOrWhiteSpace(digest) || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) return false;
        var value = digest[7..].Trim();
        if (value.Length != 64 || value.Any(ch => !Uri.IsHexDigit(ch))) return false;
        sha256 = value.ToLowerInvariant();
        return true;
    }

    private static bool IsAllowedDownloadUri(Uri uri)
        => uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
           uri.IsDefaultPort &&
           AllowedDownloadHosts.Contains(uri.Host);

    private static bool IsRedirect(HttpStatusCode status)
        => status is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod or
           HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private static string GetCurrentBuildId()
    {
        var metadata = Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key.Equals("HelpSysBuildId", StringComparison.Ordinal));
        return string.IsNullOrWhiteSpace(metadata?.Value) ? "dev" : metadata.Value.Trim().ToLowerInvariant();
    }

    private static string EscapeBatchPath(string value) => value.Replace("%", "%%", StringComparison.Ordinal);

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
    }

    public void Dispose() => _http.Dispose();
}

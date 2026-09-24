using System.Net.Http;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HelpSys.Stable;

internal sealed partial class UpdateService : IDisposable
{
    private static readonly Uri ReleaseApi = new("https://api.github.com/repos/syouziroupc/helpsys/releases/tags/preview-latest");
    private readonly HttpClient _http;

    public UpdateService()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("HelpSys-Stable/" + VersionInfo.Version);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    public async Task<UpdateInfo?> CheckAsync(CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(ReleaseApi, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new PlannerException($"更新情報を取得できませんでした (HTTP {(int)response.StatusCode})。");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var tag = document.RootElement.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() : null;
        if (!string.Equals(tag, "preview-latest", StringComparison.Ordinal))
            throw new PlannerException("通常版とは異なる更新チャネルを検出したため更新を中止しました。");

        if (!document.RootElement.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            throw new PlannerException("更新情報の形式が不正です。");

        string? newestVersion = null;
        int newestReleaseNumber = -1;
        string? newestBuildId = null;
        Uri? stableZip = null;
        Uri? shaUrl = null;

        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? "" : "";
            var url = asset.TryGetProperty("browser_download_url", out var urlProp) ? urlProp.GetString() ?? "" : "";
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !IsTrustedReleaseUri(uri)) continue;

            if (name.Equals("HelpSys-latest-win-x64.zip", StringComparison.OrdinalIgnoreCase))
                stableZip = uri;
            else if (name.Equals("HelpSys-latest-win-x64.sha256", StringComparison.OrdinalIgnoreCase))
                shaUrl = uri;
            else
            {
                var match = VersionedAssetRegex().Match(name);
                if (match.Success &&
                    int.TryParse(match.Groups["release"].Value, out var parsedRelease) &&
                    parsedRelease > newestReleaseNumber)
                {
                    newestVersion = $"A3.{parsedRelease:D4}";
                    newestReleaseNumber = parsedRelease;
                    newestBuildId = match.Groups["build"].Value.ToLowerInvariant();
                }
            }
        }

        if (newestVersion is null || newestReleaseNumber < 0 || newestBuildId is null) return null;
        if (newestReleaseNumber < VersionInfo.ReleaseNumber) return null;

        var sameVersionAndBuild =
            newestReleaseNumber == VersionInfo.ReleaseNumber &&
            newestBuildId.Equals(VersionInfo.BuildId, StringComparison.OrdinalIgnoreCase);
        if (sameVersionAndBuild) return null;

        if (stableZip is null || shaUrl is null)
            throw new PlannerException("最新版はありますが、検証付き更新ファイルが揃っていません。");

        return new UpdateInfo(newestVersion, newestReleaseNumber, newestBuildId, stableZip, shaUrl);
    }

    public async Task InstallAsync(UpdateInfo info, CancellationToken cancellationToken)
    {
        var root = Path.Combine(Path.GetTempPath(), "HelpSys-Stable-Update-" + Guid.NewGuid().ToString("N"));
        var zip = Path.Combine(root, "update.zip");
        var extracted = Path.Combine(root, "payload");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(extracted);

        try
        {
            await DownloadAsync(info.ZipUrl, zip, cancellationToken);
            var expected = (await _http.GetStringAsync(info.Sha256Url, cancellationToken))
                .Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(expected) || expected.Length != 64)
                throw new PlannerException("更新ファイルのSHA-256情報が不正です。");

            await using (var input = File.OpenRead(zip))
            {
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken)).ToLowerInvariant();
                if (!CryptographicOperations.FixedTimeEquals(
                        System.Text.Encoding.ASCII.GetBytes(actual),
                        System.Text.Encoding.ASCII.GetBytes(expected)))
                    throw new PlannerException("更新ファイルのSHA-256検証に失敗しました。更新しません。");
            }

            ZipFile.ExtractToDirectory(zip, extracted, overwriteFiles: true);
            var newExe = Path.Combine(extracted, "HelpSys.Stable.exe");
            if (!File.Exists(newExe))
                throw new PlannerException("更新パッケージにHelpSys Stable本体がありません。");

            var versionFile = Path.Combine(extracted, "VERSION.txt");
            if (!File.Exists(versionFile))
                throw new PlannerException("通常版の更新チャネル情報がないため更新を中止しました。");
            var versionText = await File.ReadAllTextAsync(versionFile, cancellationToken);
            if (!versionText.Contains("Channel: preview-latest", StringComparison.Ordinal))
                throw new PlannerException("通常版以外の更新パッケージを検出したため更新を中止しました。");

            var appDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
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

    private async Task DownloadAsync(Uri uri, string destination, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var target = File.Create(destination);
        await source.CopyToAsync(target, cancellationToken);
    }

    private static string EscapePowerShell(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    internal static bool IsTrustedReleaseUri(Uri uri)
        => uri.Scheme == Uri.UriSchemeHttps &&
           uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"^HelpSys-Stable-(?<version>\d+\.\d+\.\d+)-(?<build>[0-9a-fA-F]{8})-win-x64\.zip$",
        RegexOptions.CultureInvariant)]
    private static partial Regex VersionedAssetRegex();

    public void Dispose() => _http.Dispose();
}

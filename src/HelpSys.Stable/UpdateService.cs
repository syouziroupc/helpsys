using System.Diagnostics;
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
        if (!document.RootElement.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            throw new PlannerException("更新情報の形式が不正です。");

        System.Version? newest = null;
        Uri? stableZip = null;
        Uri? shaUrl = null;

        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? "" : "";
            var url = asset.TryGetProperty("browser_download_url", out var urlProp) ? urlProp.GetString() ?? "" : "";
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) continue;

            if (name.Equals("HelpSys-latest-win-x64.zip", StringComparison.OrdinalIgnoreCase))
                stableZip = uri;
            else if (name.Equals("HelpSys-latest-win-x64.sha256", StringComparison.OrdinalIgnoreCase))
                shaUrl = uri;
            else
            {
                var match = VersionedAssetRegex().Match(name);
                if (match.Success && System.Version.TryParse(match.Groups["version"].Value, out var parsed) &&
                    (newest is null || parsed > newest))
                    newest = parsed;
            }
        }

        if (newest is null || newest <= VersionInfo.SemanticVersion) return null;
        if (stableZip is null || shaUrl is null)
            throw new PlannerException("最新版はありますが、検証付き更新ファイルが揃っていません。");

        return new UpdateInfo(newest, stableZip, shaUrl);
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
                throw new PlannerException("更新パッケージにHelpSys本体がありません。");

            var appDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            var script = Path.Combine(root, "apply-update.ps1");
            var log = Path.Combine(root, "update.log");
            var currentPid = Environment.ProcessId;

            var scriptText = $"""
$ErrorActionPreference = 'Stop'
try {{
  Wait-Process -Id {currentPid} -ErrorAction SilentlyContinue
  Start-Sleep -Milliseconds 500
  Copy-Item -Path '{EscapePowerShell(extracted)}\*' -Destination '{EscapePowerShell(appDirectory)}' -Recurse -Force
  Start-Process -FilePath '{EscapePowerShell(Path.Combine(appDirectory, "HelpSys.Stable.exe"))}'
}} catch {{
  $_ | Out-String | Set-Content -Encoding UTF8 '{EscapePowerShell(log)}'
}}
""";
            await File.WriteAllTextAsync(script, scriptText, cancellationToken);

            Process.Start(new ProcessStartInfo
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

    [GeneratedRegex(@"^HelpSys-Stable-(?<version>\d+\.\d+\.\d+)-[0-9a-fA-F]{8}-win-x64\.zip$",
        RegexOptions.CultureInvariant)]
    private static partial Regex VersionedAssetRegex();

    public void Dispose() => _http.Dispose();
}

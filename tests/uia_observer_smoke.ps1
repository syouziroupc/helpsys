$ErrorActionPreference = 'Stop'

$exe = Join-Path $PSScriptRoot '..\src\HelpSys.Desktop\bin\Release\net10.0-windows\HelpSys.exe'
$exe = [IO.Path]::GetFullPath($exe)
if (-not (Test-Path $exe)) { throw "HelpSys.exe not found: $exe" }

$psi = [Diagnostics.ProcessStartInfo]::new()
$psi.FileName = $exe
$psi.Arguments = "--uia-observer --parent-pid $PID"
$psi.UseShellExecute = $false
$psi.CreateNoWindow = $true
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true

$p = [Diagnostics.Process]::Start($psi)
if ($null -eq $p) { throw 'Failed to start UIA observer child.' }

try {
    $request = @{
        Id = 'observer-smoke'
        Operation = 'capture'
        MaxCandidates = 8
    } | ConvertTo-Json -Compress

    $p.StandardInput.WriteLine($request)
    $p.StandardInput.Flush()

    $readTask = $p.StandardOutput.ReadLineAsync()
    if (-not $readTask.Wait([TimeSpan]::FromSeconds(8))) {
        try { $p.Kill($true) } catch {}
        throw 'UIA observer did not answer within 8 seconds.'
    }

    $line = $readTask.Result
    if ([string]::IsNullOrWhiteSpace($line)) {
        $stderr = $p.StandardError.ReadToEnd()
        throw "UIA observer returned no response. stderr=$stderr"
    }

    $response = $line | ConvertFrom-Json
    if ($response.Id -ne 'observer-smoke') { throw "UIA observer response id mismatch: $($response.Id)" }
    if ($response.Ok -ne $true) { throw "UIA observer rejected smoke request: $($response.Error)" }

    Write-Host "UIA observer process/IPC smoke passed. candidates=$(@($response.Candidates).Count)"
}
finally {
    try { $p.StandardInput.Close() } catch {}
    try {
        if (-not $p.HasExited) {
            if (-not $p.WaitForExit(1500)) { $p.Kill($true) }
        }
    } catch {}
    $p.Dispose()
}

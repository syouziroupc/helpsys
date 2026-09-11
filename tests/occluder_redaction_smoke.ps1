$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class HelpSysOccluderNative
{
    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();
}
'@

New-Item -ItemType Directory -Force -Path artifacts | Out-Null
$exe = Resolve-Path 'smoke-bin/normal/HelpSys.exe'
$mock = $null
$target = $null
$overlay = $null
$helpSys = $null
$overlayStdout = 'artifacts/occluder-overlay-stdout.txt'
$overlayStderr = 'artifacts/occluder-overlay-stderr.txt'

function Find-Element([System.Diagnostics.Process]$process, [string]$automationId) {
  if ($null -eq $process -or $process.HasExited) { return $null }
  $root = [System.Windows.Automation.AutomationElement]::RootElement
  $processCondition = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
    $process.Id)
  $idCondition = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
    $automationId)
  $condition = New-Object System.Windows.Automation.AndCondition($processCondition, $idCondition)
  return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Find-SmokeTargetWindow([System.Diagnostics.Process]$process) {
  if ($null -eq $process -or $process.HasExited) { return $null }
  $root = [System.Windows.Automation.AutomationElement]::RootElement
  $processCondition = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
    $process.Id)
  $nameCondition = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::NameProperty,
    'HelpSys Smoke Target')
  $condition = New-Object System.Windows.Automation.AndCondition($processCondition, $nameCondition)
  return $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $condition)
}

function Activate-SmokeTarget([System.Diagnostics.Process]$process) {
  $deadline = [DateTime]::UtcNow.AddSeconds(6)
  $targetWindow = $null
  while ($null -eq $targetWindow -and [DateTime]::UtcNow -lt $deadline) {
    if ($process.HasExited) { throw 'Smoke target exited before its work surface could be activated.' }
    $targetWindow = Find-SmokeTargetWindow $process
    if ($null -eq $targetWindow) { Start-Sleep -Milliseconds 100 }
  }
  if ($null -eq $targetWindow) { throw 'Could not locate the HelpSys Smoke Target top-level window.' }

  $hwnd = [IntPtr]$targetWindow.Current.NativeWindowHandle
  if ($hwnd -eq [IntPtr]::Zero) { throw 'Smoke target UI Automation element did not expose a native window handle.' }

  [void][HelpSysOccluderNative]::ShowWindow($hwnd, 5)
  [void][HelpSysOccluderNative]::SetForegroundWindow($hwnd)
  Start-Sleep -Milliseconds 350

  $foreground = [HelpSysOccluderNative]::GetForegroundWindow()
  if ($foreground -ne $hwnd) {
    throw "Occluder harness could not make the real smoke target foreground. expected=$hwnd actual=$foreground"
  }
  return $hwnd
}

function Save-DesktopScreenshot([string]$name) {
  $bounds = [System.Windows.Forms.SystemInformation]::VirtualScreen
  $bitmap = New-Object System.Drawing.Bitmap $bounds.Width, $bounds.Height
  $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
  try {
    $graphics.CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)
    $bitmap.Save((Join-Path $PWD "artifacts/$name"), [System.Drawing.Imaging.ImageFormat]::Png)
  }
  finally {
    $graphics.Dispose()
    $bitmap.Dispose()
  }
}

function Get-OverlayFailureDetail {
  if (Test-Path $overlayStderr) {
    $detail = (Get-Content $overlayStderr -Raw -ErrorAction SilentlyContinue).Trim()
    if (-not [string]::IsNullOrWhiteSpace($detail)) { return $detail }
  }
  return 'no stderr was captured'
}

function Assert-CenterIsRedacted([string]$path) {
  $resolved = (Resolve-Path $path).Path
  $bitmap = [System.Drawing.Bitmap]::new($resolved)
  try {
    $left = [Math]::Max(0, [int]($bitmap.Width * 0.34))
    $right = [Math]::Min($bitmap.Width - 1, [int]($bitmap.Width * 0.66))
    $top = [Math]::Max(0, [int]($bitmap.Height * 0.38))
    $bottom = [Math]::Min($bitmap.Height - 1, [int]($bitmap.Height * 0.62))
    $black = 0
    $total = 0

    for ($y = $top; $y -le $bottom; $y += 2) {
      for ($x = $left; $x -le $right; $x += 2) {
        $pixel = $bitmap.GetPixel($x, $y)
        $total++
        if ($pixel.R -le 8 -and $pixel.G -le 8 -and $pixel.B -le 8) { $black++ }
      }
    }

    if ($total -le 0) { throw 'Occluder pixel probe had no samples.' }
    $ratio = $black / [double]$total
    if ($ratio -lt 0.85) {
      throw "The unrelated centered overlay was not strongly redacted in the exact outbound screenshot. blackRatio=$ratio"
    }
  }
  finally {
    $bitmap.Dispose()
  }
}

try {
  Remove-Item 'artifacts/mock-last-request.json' -Force -ErrorAction SilentlyContinue
  Remove-Item 'artifacts/helpsys-occluder-egress-image.png' -Force -ErrorAction SilentlyContinue
  Remove-Item 'artifacts/occluder-overlay-bounds.json' -Force -ErrorAction SilentlyContinue
  Remove-Item $overlayStdout -Force -ErrorAction SilentlyContinue
  Remove-Item $overlayStderr -Force -ErrorAction SilentlyContinue
  $env:HELPSYS_API_BASE = 'http://127.0.0.1:8765'

  $mock = Start-Process python -ArgumentList 'tests/mock_guide_server.py' -PassThru -WindowStyle Hidden
  Start-Sleep -Seconds 1

  $target = Start-Process powershell.exe -ArgumentList '-NoProfile','-STA','-ExecutionPolicy','Bypass','-File','tests/smoke_target.ps1' -PassThru
  Start-Sleep -Seconds 2
  if ($target.HasExited) { throw 'Smoke target exited before occluder test.' }

  # GitHub-hosted Windows runners can keep Windows Terminal foreground even though the dedicated
  # WinForms target is visible. Force the exact target HWND foreground BEFORE creating the
  # non-activating overlay. The later overlay must stay visually above it without changing focus.
  $targetHwnd = Activate-SmokeTarget $target

  $overlay = Start-Process powershell.exe `
    -ArgumentList '-NoProfile','-STA','-ExecutionPolicy','Bypass','-File','tests/occluder_smoke_overlay.ps1' `
    -RedirectStandardOutput $overlayStdout `
    -RedirectStandardError $overlayStderr `
    -PassThru
  $overlayDeadline = [DateTime]::UtcNow.AddSeconds(8)
  while (-not (Test-Path 'artifacts/occluder-overlay-bounds.json') -and [DateTime]::UtcNow -lt $overlayDeadline) {
    if ($overlay.HasExited) {
      throw "No-activate overlay exited before becoming visible: $(Get-OverlayFailureDetail)"
    }
    Start-Sleep -Milliseconds 150
  }
  if (-not (Test-Path 'artifacts/occluder-overlay-bounds.json')) {
    throw "No-activate overlay did not become visible: $(Get-OverlayFailureDetail)"
  }
  if ($overlay.Id -eq $target.Id) { throw 'Occluder smoke requires a distinct overlay process.' }

  $overlayBounds = Get-Content 'artifacts/occluder-overlay-bounds.json' -Raw -Encoding UTF8 | ConvertFrom-Json
  if ($overlayBounds.showActivated -ne $false -or $overlayBounds.topmost -ne $true) {
    throw 'Occluder helper did not preserve the required non-activating TopMost test condition.'
  }

  Start-Sleep -Milliseconds 250
  $foregroundAfterOverlay = [HelpSysOccluderNative]::GetForegroundWindow()
  if ($foregroundAfterOverlay -ne $targetHwnd) {
    throw "No-activate overlay unexpectedly stole foreground from the real target. expected=$targetHwnd actual=$foregroundAfterOverlay"
  }

  Save-DesktopScreenshot 'helpsys-occluder-source.png'

  $helpSys = Start-Process $exe -PassThru
  Start-Sleep -Seconds 5
  if ($helpSys.HasExited) { throw 'HelpSys exited before occluder smoke.' }

  $request = Find-Element $helpSys 'RequestBox'
  $guide = Find-Element $helpSys 'GuideButton'
  if ($null -eq $request -or $null -eq $guide) { throw 'HelpSys controls missing during occluder smoke.' }

  $request.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('OCCLUDER redaction smoke: open the test target')
  $guide.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()

  $deadline = [DateTime]::UtcNow.AddSeconds(15)
  while (-not (Test-Path 'artifacts/helpsys-occluder-egress-image.png') -and [DateTime]::UtcNow -lt $deadline) {
    if ($helpSys.HasExited) { throw 'HelpSys exited before producing the occluder outbound screenshot.' }
    Start-Sleep -Milliseconds 200
  }

  if (-not (Test-Path 'artifacts/helpsys-occluder-egress-image.png')) {
    Save-DesktopScreenshot 'helpsys-occluder-failure.png'
    throw 'Mock endpoint did not receive the exact outbound occluder smoke screenshot.'
  }
  if (-not (Test-Path 'artifacts/mock-last-request.json')) { throw 'Occluder smoke diagnostics are missing.' }

  $diagnosticsRaw = Get-Content 'artifacts/mock-last-request.json' -Raw -Encoding UTF8
  $diagnostics = $diagnosticsRaw | ConvertFrom-Json
  if ($diagnostics.path -ne '/v1/quality-guide') { throw "Occluder smoke used wrong route: $($diagnostics.path)" }
  if ($diagnostics.hasScreenshot -ne $true) { throw 'Occluder smoke did not send a screenshot.' }
  if ($diagnostics.eligible.automationId -notcontains 'SmokeButton') {
    throw "Occluder smoke lost the real target surface and did not preserve SmokeButton. foreground=$($diagnostics.foreground) pid=$($diagnostics.foregroundProcessId)"
  }
  if ($diagnosticsRaw.Contains('UNRELATED OVERLAY', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Unrelated overlay text leaked into outbound structured UI evidence.'
  }

  Assert-CenterIsRedacted 'artifacts/helpsys-occluder-egress-image.png'
  Write-Host 'HelpSys process-bound foreground and exact outbound occluder redaction smoke passed.'
}
finally {
  Remove-Item Env:HELPSYS_API_BASE -ErrorAction SilentlyContinue
  if ($null -ne $helpSys -and -not $helpSys.HasExited) { Stop-Process -Id $helpSys.Id -Force }
  if ($null -ne $overlay -and -not $overlay.HasExited) { Stop-Process -Id $overlay.Id -Force }
  if ($null -ne $target -and -not $target.HasExited) { Stop-Process -Id $target.Id -Force }
  if ($null -ne $mock -and -not $mock.HasExited) { Stop-Process -Id $mock.Id -Force }
}

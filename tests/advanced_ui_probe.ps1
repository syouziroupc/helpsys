$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path artifacts | Out-Null

$mock = Start-Process python -ArgumentList 'tests/mock_guide_server.py' -PassThru -WindowStyle Hidden
$env:HELPSYS_API_BASE = 'http://127.0.0.1:8765'
Start-Sleep -Seconds 1
$target = Start-Process powershell.exe -ArgumentList '-NoProfile','-STA','-ExecutionPolicy','Bypass','-File','tests/advanced_virtual_target.ps1' -PassThru
Start-Sleep -Seconds 3
$exe = Resolve-Path 'src/HelpSys.Desktop/bin/Release/net10.0-windows/HelpSys.exe'
$help = Start-Process $exe -PassThru

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName WindowsBase
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class HelpSysMouseProbeV2 {
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);
}
'@

function Find-Element([int] $processId, [string] $automationId) {
  $root = [System.Windows.Automation.AutomationElement]::RootElement
  $pc = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $processId)
  $ic = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $automationId)
  return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.AndCondition($pc,$ic)))
}

function Is-CoveredByHelpSys([int] $helpProcessId, [double] $x, [double] $y) {
  $root = [System.Windows.Automation.AutomationElement]::RootElement
  $pc = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $helpProcessId)
  $windows = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $pc)
  foreach ($window in $windows) {
    try {
      $r = $window.Current.BoundingRectangle
      if (-not $r.IsEmpty -and $x -ge $r.Left -and $x -le $r.Right -and $y -ge $r.Top -and $y -le $r.Bottom) { return $true }
    } catch { }
  }
  return $false
}

try {
  Start-Sleep -Seconds 4
  if ($help.HasExited) { throw 'HelpSys exited before advanced UI test.' }
  $box = Find-Element $help.Id 'RequestBox'
  $guide = Find-Element $help.Id 'GuideButton'
  if ($null -eq $box -or $null -eq $guide) { throw 'HelpSys core controls missing.' }
  $box.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('open the test target')
  $guide.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
  Start-Sleep -Seconds 10

  if (-not (Test-Path 'artifacts/mock-last-request.json')) { throw 'Advanced planner request was not captured.' }
  if (-not (Test-Path 'artifacts/mock-last-image.png')) { throw 'Advanced planner screenshot was not captured.' }
  $jsonText = Get-Content 'artifacts/mock-last-request.json' -Raw -Encoding UTF8
  if ($jsonText.Contains('PRIVATE-PROBE-847251')) { throw 'Raw populated input value leaked into structured planner diagnostics.' }
  $diag = $jsonText | ConvertFrom-Json
  $probe = @($diag.eligible | Where-Object { $_.automationId -eq 'ProbeInput' }) | Select-Object -First 1
  if ($null -eq $probe) { throw 'ProbeInput was not present in planner UIA evidence.' }
  if ($probe.value -ne '<input-present>') { throw "Planner did not receive the privacy-safe populated marker. value=$($probe.value)" }
  Write-Host 'STRUCTURED_PRIVACY=PASS'

  $probeElement = Find-Element $target.Id 'ProbeInput'
  if ($null -eq $probeElement) { throw 'ProbeInput UIA element missing from target.' }
  $rect = $probeElement.Current.BoundingRectangle
  $imagePath = (Resolve-Path 'artifacts/mock-last-image.png').Path
  $bitmap = [System.Drawing.Bitmap]::new($imagePath)
  try {
    $center = [System.Drawing.Point]::new([int]($rect.Left + $rect.Width / 2), [int]($rect.Top + $rect.Height / 2))
    $monitor = [System.Windows.Forms.Screen]::FromPoint($center).Bounds
    $scaleX = $bitmap.Width / [double]$monitor.Width
    $scaleY = $bitmap.Height / [double]$monitor.Height
    $relativeLeft = $rect.Left - $monitor.Left
    $relativeTop = $rect.Top - $monitor.Top
    $left = [Math]::Max(0, [int](($relativeLeft + $rect.Width * 0.20) * $scaleX))
    $right = [Math]::Min($bitmap.Width - 1, [int](($relativeLeft + $rect.Width * 0.80) * $scaleX))
    $top = [Math]::Max(0, [int](($relativeTop + $rect.Height * 0.25) * $scaleY))
    $bottom = [Math]::Min($bitmap.Height - 1, [int](($relativeTop + $rect.Height * 0.75) * $scaleY))
    $black = 0
    $total = 0
    for ($x = $left; $x -le $right; $x += 3) {
      for ($y = $top; $y -le $bottom; $y += 2) {
        $pixel = $bitmap.GetPixel($x,$y)
        $total++
        if ($pixel.R -le 12 -and $pixel.G -le 12 -and $pixel.B -le 12) { $black++ }
      }
    }
    if ($total -le 0) { throw 'Privacy pixel sample was empty.' }
    $ratio = $black / [double]$total
    Write-Host "PRIVATE_INPUT_BLACK_RATIO=$ratio"
    if ($ratio -lt 0.85) { throw "Populated input was not black-redacted in the actual planner screenshot. ratio=$ratio" }
    Write-Host 'SCREENSHOT_PRIVACY=PASS'
  }
  finally { $bitmap.Dispose() }

  $wrong = Find-Element $target.Id 'WrongButton'
  if ($null -eq $wrong) { throw 'WrongButton UIA element missing.' }
  $candidates = @($wrong, $probeElement)
  $clickPoint = $null
  foreach ($candidate in $candidates) {
    $r = $candidate.Current.BoundingRectangle
    $cx = $r.Left + $r.Width / 2
    $cy = $r.Top + $r.Height / 2
    if (-not (Is-CoveredByHelpSys $help.Id $cx $cy)) {
      $clickPoint = [System.Drawing.Point]::new([int]$cx,[int]$cy)
      break
    }
  }
  if ($null -eq $clickPoint) { throw 'Could not find an off-route physical click point outside HelpSys windows.' }

  [HelpSysMouseProbeV2]::SetCursorPos($clickPoint.X, $clickPoint.Y) | Out-Null
  [HelpSysMouseProbeV2]::mouse_event(0x0002,0,0,0,[UIntPtr]::Zero)
  Start-Sleep -Milliseconds 100
  [HelpSysMouseProbeV2]::mouse_event(0x0004,0,0,0,[UIntPtr]::Zero)
  Start-Sleep -Seconds 8

  $recovery = Get-Content 'artifacts/mock-last-request.json' -Raw -Encoding UTF8 | ConvertFrom-Json
  if ($recovery.recoveryMode -ne $true) { throw 'Physical off-route click did not trigger recoveryMode.' }
  if ([string]::IsNullOrWhiteSpace([string]$recovery.routeIssue)) { throw 'Recovery request did not carry a routeIssue.' }
  Write-Host "OFF_ROUTE_RECOVERY_ISSUE=$($recovery.routeIssue)"
  Write-Host 'OFF_ROUTE_RECOVERY=PASS'
}
finally {
  if ($null -ne $help -and -not $help.HasExited) { Stop-Process -Id $help.Id -Force }
  if ($null -ne $target -and -not $target.HasExited) { Stop-Process -Id $target.Id -Force }
  if ($null -ne $mock -and -not $mock.HasExited) { Stop-Process -Id $mock.Id -Force }
}

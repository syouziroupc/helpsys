$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path artifacts | Out-Null
Remove-Item 'artifacts/mock-last-request.json','artifacts/mock-last-image.png','artifacts/mock-request-count.txt' -ErrorAction SilentlyContinue
Remove-Item 'artifacts/virtual-target-*.flag' -ErrorAction SilentlyContinue

$targetProject = 'tests/HelpSys.VirtualTarget/HelpSys.VirtualTarget.csproj'
dotnet build $targetProject -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Virtual target build failed.' }
$targetExe = (Resolve-Path 'tests/HelpSys.VirtualTarget/bin/Release/net10.0-windows/HelpSys.VirtualTarget.exe').Path

$mock = Start-Process python -ArgumentList 'tests/mock_guide_server.py' -PassThru -WindowStyle Hidden
$env:HELPSYS_API_BASE = 'http://127.0.0.1:8765'
Start-Sleep -Seconds 1
$target = Start-Process $targetExe -WorkingDirectory (Get-Location).Path -PassThru
$help = $null

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

function Wait-Element([int] $processId, [string] $automationId, [int] $seconds = 12) {
  $deadline = [DateTime]::UtcNow.AddSeconds($seconds)
  do {
    if ($null -ne $target -and $target.HasExited) { throw "Virtual target exited unexpectedly with code $($target.ExitCode)." }
    $found = Find-Element $processId $automationId
    if ($null -ne $found) { return $found }
    Start-Sleep -Milliseconds 150
  } while ([DateTime]::UtcNow -lt $deadline)
  return $null
}

function Wait-ElementGone([int] $processId, [string] $automationId, [int] $seconds = 10) {
  $deadline = [DateTime]::UtcNow.AddSeconds($seconds)
  do {
    if ($null -eq (Find-Element $processId $automationId)) { return $true }
    Start-Sleep -Milliseconds 150
  } while ([DateTime]::UtcNow -lt $deadline)
  return $false
}

function Request-Count {
  if (-not (Test-Path 'artifacts/mock-request-count.txt')) { return 0 }
  return [int](Get-Content 'artifacts/mock-request-count.txt' -Raw)
}

function Read-Diagnostics {
  if (-not (Test-Path 'artifacts/mock-last-request.json')) { return $null }
  return Get-Content 'artifacts/mock-last-request.json' -Raw -Encoding UTF8 | ConvertFrom-Json
}

function Wait-RequestAfter([int] $baseline, [int] $seconds = 15) {
  $deadline = [DateTime]::UtcNow.AddSeconds($seconds)
  while ([DateTime]::UtcNow -lt $deadline) {
    if ((Request-Count) -gt $baseline) { return Read-Diagnostics }
    if ($null -ne $help -and $help.HasExited) { throw "HelpSys exited unexpectedly with code $($help.ExitCode)." }
    Start-Sleep -Milliseconds 180
  }
  return $null
}

function Wait-FlagConsumed([string] $path, [int] $seconds = 5) {
  $deadline = [DateTime]::UtcNow.AddSeconds($seconds)
  while (Test-Path $path) {
    if ([DateTime]::UtcNow -ge $deadline) { return $false }
    Start-Sleep -Milliseconds 100
  }
  return $true
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
  $probeElement = Wait-Element $target.Id 'ProbeInput'
  if ($null -eq $probeElement) { throw "Deterministic ProbeInput was not exposed by target pid=$($target.Id)." }
  Write-Host "ADVANCED_TARGET_PID=$($target.Id)"

  $exe = (Resolve-Path 'src/HelpSys.Desktop/bin/Release/net10.0-windows/HelpSys.exe').Path
  $help = Start-Process $exe -PassThru

  $box = $null
  $guide = $null
  $deadline = [DateTime]::UtcNow.AddSeconds(15)
  while ([DateTime]::UtcNow -lt $deadline) {
    if ($help.HasExited) { throw "HelpSys exited before advanced UI test with code $($help.ExitCode)." }
    $box = Find-Element $help.Id 'RequestBox'
    $guide = Find-Element $help.Id 'GuideButton'
    if ($null -ne $box -and $null -ne $guide) { break }
    Start-Sleep -Milliseconds 180
  }
  if ($null -eq $box -or $null -eq $guide) { throw 'HelpSys core controls missing.' }

  $box.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('open the test target')
  $guide.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()

  $initial = Wait-RequestAfter 0 24
  if ($null -eq $initial) { throw 'Advanced planner request was not captured.' }
  $deadline = [DateTime]::UtcNow.AddSeconds(8)
  while (-not (Test-Path 'artifacts/mock-last-image.png') -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 150 }
  if (-not (Test-Path 'artifacts/mock-last-image.png')) { throw 'Advanced planner screenshot was not captured.' }

  if ([int]$initial.foregroundProcessId -ne $target.Id) {
    throw "Planner observed wrong foreground process. expected=$($target.Id) actual=$($initial.foregroundProcessId) foreground=$($initial.foreground)"
  }
  Write-Host 'FOREGROUND_TARGET_IDENTITY=PASS'

  $jsonText = Get-Content 'artifacts/mock-last-request.json' -Raw -Encoding UTF8
  if ($jsonText.Contains('PRIVATE-PROBE-847251')) { throw 'Raw populated input value leaked into structured planner diagnostics.' }
  if ($jsonText.Contains('PRIVATE-WINDOW-TITLE-319751')) { throw 'Raw foreground window title leaked into structured planner diagnostics.' }
  $probe = @($initial.eligible | Where-Object { $_.automationId -eq 'ProbeInput' }) | Select-Object -First 1
  if ($null -eq $probe) { throw 'ProbeInput was not present in planner UIA evidence.' }
  if ($probe.value -ne '<input-present>') { throw "Planner did not receive the privacy-safe populated marker. value=$($probe.value)" }
  Write-Host 'STRUCTURED_PRIVACY=PASS'

  if ($null -eq $initial.captureBounds) { throw 'Quality request did not include screenshot captureBounds.' }
  $captureLeft = [double]$initial.captureBounds.x
  $captureTop = [double]$initial.captureBounds.y
  $captureRight = $captureLeft + [double]$initial.captureBounds.width
  $captureBottom = $captureTop + [double]$initial.captureBounds.height
  $rect = $probeElement.Current.BoundingRectangle
  $centerX = $rect.Left + $rect.Width / 2
  $centerY = $rect.Top + $rect.Height / 2
  if ($centerX -lt $captureLeft -or $centerX -gt $captureRight -or $centerY -lt $captureTop -or $centerY -gt $captureBottom) {
    throw "ProbeInput is outside the screenshot capture coordinate system. capture=$captureLeft,$captureTop..$captureRight,$captureBottom target=$centerX,$centerY"
  }
  Write-Host 'CAPTURE_BOUNDS_MAPPING=PASS'

  $imagePath = (Resolve-Path 'artifacts/mock-last-image.png').Path
  $bitmap = [System.Drawing.Bitmap]::new($imagePath)
  try {
    $monitor = [System.Windows.Forms.Screen]::FromPoint([System.Drawing.Point]::new([int]$centerX, [int]$centerY)).Bounds
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

  # A title-only update must not be mistaken for a new modal/window. This is a pure target-side
  # change triggered without mouse/keyboard input so the user-action observer cannot explain it.
  $titleBaseline = Request-Count
  Set-Content 'artifacts/virtual-target-title-flip.flag' '1' -Encoding ascii
  if (-not (Wait-FlagConsumed 'artifacts/virtual-target-title-flip.flag')) { throw 'Virtual target did not consume title flip command.' }
  Start-Sleep -Seconds 3
  $afterTitle = Request-Count
  Write-Host "TITLE_ONLY_BASELINE=$titleBaseline TITLE_ONLY_AFTER=$afterTitle"
  if ($afterTitle -ne $titleBaseline) { throw 'Title-only window text change incorrectly invalidated otherwise stable guidance.' }
  Write-Host 'TITLE_ONLY_STABILITY=PASS'

  # A state-only checkbox change is semantically meaningful even though control topology is the same.
  $toggleBaseline = Request-Count
  Set-Content 'artifacts/virtual-target-toggle.flag' '1' -Encoding ascii
  if (-not (Wait-FlagConsumed 'artifacts/virtual-target-toggle.flag')) { throw 'Virtual target did not consume toggle command.' }
  $toggleDiag = Wait-RequestAfter $toggleBaseline 14
  if ($null -eq $toggleDiag) { throw 'State-only checkbox change did not trigger replanning.' }
  $semanticHistory = @($toggleDiag.history | Where-Object { $_.action -eq 'semantic_state_changed' })
  if ($semanticHistory.Count -lt 1) { throw 'State-only checkbox change replanned without semantic_state_changed history.' }
  Write-Host 'SEMANTIC_STATE_REPLAN=PASS'

  # A new modal in the SAME PROCESS must still invalidate guidance. Process-id checks alone are not enough.
  $modalBaseline = Request-Count
  Set-Content 'artifacts/virtual-target-open-modal.flag' '1' -Encoding ascii
  if (-not (Wait-FlagConsumed 'artifacts/virtual-target-open-modal.flag')) { throw 'Virtual target did not consume modal command.' }
  $closeModal = Wait-Element $target.Id 'CloseModalButton' 8
  if ($null -eq $closeModal) { throw 'Same-process modal did not appear.' }
  $modalDiag = Wait-RequestAfter $modalBaseline 14
  if ($null -eq $modalDiag) { throw 'Same-process modal did not invalidate stale guidance.' }
  $windowHistory = @($modalDiag.history | Where-Object { $_.action -eq 'window_set_changed' })
  if ($windowHistory.Count -lt 1) { throw 'Same-process modal was not classified as a window-set change.' }
  if ([int]$modalDiag.foregroundProcessId -ne $target.Id) { throw 'Modal probe unexpectedly changed process identity.' }
  Write-Host 'SAME_PROCESS_MODAL_REPLAN=PASS'

  $closeModal.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
  if (-not (Wait-ElementGone $target.Id 'CloseModalButton' 8)) { throw 'Probe modal did not close.' }
  $afterModalCloseBaseline = Request-Count
  $postClose = Wait-RequestAfter $afterModalCloseBaseline 10
  if ($null -eq $postClose) {
    # Closing may be observed before the baseline read on a fast runner. It is enough that the modal
    # is gone; give the live planner a short settling interval before the physical off-route probe.
    Start-Sleep -Seconds 2
  }

  $wrong = Wait-Element $target.Id 'WrongButton'
  if ($null -eq $wrong) { throw 'WrongButton UIA element missing.' }
  $wrongRect = $wrong.Current.BoundingRectangle
  $cx = $wrongRect.Left + $wrongRect.Width / 2
  $cy = $wrongRect.Top + $wrongRect.Height / 2
  if (Is-CoveredByHelpSys $help.Id $cx $cy) { throw 'WrongButton is covered by HelpSys; physical off-route click would be invalid.' }

  $offRouteBaseline = Request-Count
  [HelpSysMouseProbeV2]::SetCursorPos([int]$cx, [int]$cy) | Out-Null
  [HelpSysMouseProbeV2]::mouse_event(0x0002,0,0,0,[UIntPtr]::Zero)
  Start-Sleep -Milliseconds 100
  [HelpSysMouseProbeV2]::mouse_event(0x0004,0,0,0,[UIntPtr]::Zero)

  $deadline = [DateTime]::UtcNow.AddSeconds(14)
  $recovery = $null
  while ([DateTime]::UtcNow -lt $deadline) {
    if ((Request-Count) -gt $offRouteBaseline) {
      $candidate = Read-Diagnostics
      if ($candidate.recoveryMode -eq $true) { $recovery = $candidate; break }
    }
    Start-Sleep -Milliseconds 180
  }
  if ($null -eq $recovery) { throw 'Physical off-route click did not trigger recoveryMode.' }
  if ([string]::IsNullOrWhiteSpace([string]$recovery.routeIssue)) { throw 'Recovery request did not carry a routeIssue.' }
  Write-Host "OFF_ROUTE_RECOVERY_ISSUE=$($recovery.routeIssue)"
  Write-Host 'OFF_ROUTE_RECOVERY=PASS'
}
finally {
  Remove-Item 'artifacts/virtual-target-*.flag' -ErrorAction SilentlyContinue
  if ($null -ne $help -and -not $help.HasExited) { Stop-Process -Id $help.Id -Force }
  if ($null -ne $target -and -not $target.HasExited) { Stop-Process -Id $target.Id -Force }
  if ($null -ne $mock -and -not $mock.HasExited) { Stop-Process -Id $mock.Id -Force }
}

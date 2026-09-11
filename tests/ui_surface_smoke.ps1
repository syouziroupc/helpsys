$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

New-Item -ItemType Directory -Force -Path artifacts | Out-Null
$exe = Resolve-Path 'smoke-bin/normal/HelpSys.exe'
$mock = $null
$target = $null
$helpSys = $null

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

function Find-MainWindow([System.Diagnostics.Process]$process) {
  if ($null -eq $process -or $process.HasExited) { return $null }
  $root = [System.Windows.Automation.AutomationElement]::RootElement
  $processCondition = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
    $process.Id)
  $windowCondition = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::Window)
  $condition = New-Object System.Windows.Automation.AndCondition($processCondition, $windowCondition)
  return $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $condition)
}

function Save-Screenshot([string]$name) {
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

function Center-Y($rect) { return $rect.Top + ($rect.Height / 2.0) }
function Assert-InWindow($windowRect, $rect, [string]$name) {
  if ($rect.IsEmpty) { throw "$name has an empty UI Automation rectangle." }
  if ($rect.Left -lt ($windowRect.Left - 2) -or $rect.Right -gt ($windowRect.Right + 2) -or
      $rect.Top -lt ($windowRect.Top - 2) -or $rect.Bottom -gt ($windowRect.Bottom + 2)) {
    throw "$name is clipped outside the HelpSys window. Window=$windowRect Control=$rect"
  }
}

try {
  Remove-Item 'artifacts/mock-last-request.json' -Force -ErrorAction SilentlyContinue
  $env:HELPSYS_API_BASE = 'http://127.0.0.1:8765'
  $mock = Start-Process python -ArgumentList 'tests/mock_guide_server.py' -PassThru -WindowStyle Hidden
  Start-Sleep -Seconds 1

  $target = Start-Process powershell.exe -ArgumentList '-NoProfile','-STA','-ExecutionPolicy','Bypass','-File','tests/smoke_target.ps1' -PassThru
  Start-Sleep -Seconds 2

  $startupMaximumMs = 4000
  $guideMaximumMs = 5000
  $startup = [System.Diagnostics.Stopwatch]::StartNew()
  $helpSys = Start-Process $exe -PassThru

  $ids = @('RequestBox','VoiceButton','SpeakButton','ClearButton','GuideButton','PrivacyButton','CommanderButton','ExitButton','StateText')
  $controls = @{}
  $window = $null
  while ($startup.Elapsed.TotalMilliseconds -lt $startupMaximumMs) {
    if ($helpSys.HasExited) { throw 'HelpSys exited during UI surface startup smoke.' }
    $window = Find-MainWindow $helpSys
    foreach ($id in $ids) {
      if (-not $controls.ContainsKey($id) -or $null -eq $controls[$id]) {
        $controls[$id] = Find-Element $helpSys $id
      }
    }
    $missing = @($ids | Where-Object { $null -eq $controls[$_] })
    if ($null -ne $window -and $missing.Count -eq 0) { break }
    Start-Sleep -Milliseconds 50
  }
  $startup.Stop()

  $missing = @($ids | Where-Object { $null -eq $controls[$_] })
  if ($null -eq $window -or $missing.Count -gt 0) {
    Save-Screenshot 'helpsys-ui-startup-failure.png'
    throw "Core UI did not become available within $startupMaximumMs ms. Missing: $($missing -join ', ')"
  }

  foreach ($id in @('RequestBox','VoiceButton','SpeakButton','ClearButton','GuideButton','PrivacyButton','CommanderButton','ExitButton')) {
    if (-not $controls[$id].Current.IsEnabled) {
      throw "Normal HelpSys surface feature unexpectedly became disabled: $id"
    }
  }

  $windowRect = $window.Current.BoundingRectangle
  if ($windowRect.Width -lt 580 -or $windowRect.Width -gt 650) {
    throw "HelpSys width regressed from the compact surface: $($windowRect.Width)"
  }
  if ($windowRect.Height -gt 150) {
    Save-Screenshot 'helpsys-ui-height-regression.png'
    throw "Idle HelpSys surface is too tall: $($windowRect.Height) px"
  }

  foreach ($id in $ids) { Assert-InWindow $windowRect $controls[$id].Current.BoundingRectangle $id }

  $requestRect = $controls['RequestBox'].Current.BoundingRectangle
  $voiceRect = $controls['VoiceButton'].Current.BoundingRectangle
  $speakRect = $controls['SpeakButton'].Current.BoundingRectangle
  $clearRect = $controls['ClearButton'].Current.BoundingRectangle
  $guideRect = $controls['GuideButton'].Current.BoundingRectangle
  $privacyRect = $controls['PrivacyButton'].Current.BoundingRectangle
  $exitRect = $controls['ExitButton'].Current.BoundingRectangle
  $commanderRect = $controls['CommanderButton'].Current.BoundingRectangle
  $stateRect = $controls['StateText'].Current.BoundingRectangle

  foreach ($pair in @(
    @($requestRect,$voiceRect,'RequestBox/VoiceButton'),
    @($voiceRect,$speakRect,'VoiceButton/SpeakButton'),
    @($speakRect,$clearRect,'SpeakButton/ClearButton'),
    @($clearRect,$guideRect,'ClearButton/GuideButton')
  )) {
    if ($pair[0].Right -gt ($pair[1].Left + 2)) { throw "Primary controls overlap: $($pair[2])" }
  }

  if ((Center-Y $privacyRect) -ge ((Center-Y $requestRect) - 8)) {
    throw 'Privacy control slipped back into the primary/footer interaction band instead of staying in the title row.'
  }
  if ($privacyRect.Right -gt ($exitRect.Left + 2)) { throw 'Privacy and Exit controls overlap.' }
  if ((Center-Y $commanderRect) -le ((Center-Y $requestRect) + 8)) { throw 'Commander no longer occupies the footer/status band.' }
  if ($stateRect.Width -lt 390) { throw "Normal status text area is too narrow: $($stateRect.Width) px" }
  if ($stateRect.Height -gt 24) {
    Save-Screenshot 'helpsys-ui-status-wrap-regression.png'
    throw "Normal idle status wrapped beyond one compact line: $($stateRect.Height) px"
  }
  if ($requestRect.Height -lt 34 -or $requestRect.Height -gt 44 -or $guideRect.Height -lt 34 -or $guideRect.Height -gt 44) {
    throw 'Primary interaction controls no longer retain the established ~38px height.'
  }

  $normalState = $controls['StateText'].Current.Name
  if ($normalState -like '*安全版*' -or $normalState -like '*無効*') {
    throw "Normal edition surfaced a Safe/disabled state unexpectedly: $normalState"
  }

  Save-Screenshot 'helpsys-ui-surface.png'

  $valuePattern = $controls['RequestBox'].GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
  $valuePattern.SetValue('open the test target')
  $invokePattern = $controls['GuideButton'].GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)

  $guideTimer = [System.Diagnostics.Stopwatch]::StartNew()
  $invokePattern.Invoke()
  while ($guideTimer.Elapsed.TotalMilliseconds -lt $guideMaximumMs -and -not (Test-Path 'artifacts/mock-last-request.json')) {
    if ($helpSys.HasExited) { throw 'HelpSys exited during perceived-performance smoke.' }
    Start-Sleep -Milliseconds 100
  }
  $guideTimer.Stop()

  if (-not (Test-Path 'artifacts/mock-last-request.json')) {
    Save-Screenshot 'helpsys-ui-guide-latency-failure.png'
    throw "Guide action did not reach the local planner within $guideMaximumMs ms. Elapsed=$([math]::Round($guideTimer.Elapsed.TotalMilliseconds)) ms"
  }

  $diagnostics = Get-Content 'artifacts/mock-last-request.json' -Raw -Encoding UTF8 | ConvertFrom-Json
  if ($diagnostics.path -ne '/v1/quality-guide' -or $diagnostics.hasScreenshot -ne $true) {
    throw 'UI performance smoke reached the wrong route or lost the quality screenshot.'
  }

  $metrics = [ordered]@{
    startupCoreUiMilliseconds = [math]::Round($startup.Elapsed.TotalMilliseconds)
    startupMaximumMilliseconds = $startupMaximumMs
    idleWindowWidth = [math]::Round($windowRect.Width)
    idleWindowHeight = [math]::Round($windowRect.Height)
    statusAreaWidth = [math]::Round($stateRect.Width)
    statusAreaHeight = [math]::Round($stateRect.Height)
    guideToLocalApiMilliseconds = [math]::Round($guideTimer.Elapsed.TotalMilliseconds)
    guideMaximumMilliseconds = $guideMaximumMs
  }
  $metrics | ConvertTo-Json | Set-Content 'artifacts/helpsys-ui-performance.json' -Encoding UTF8
  Write-Host "HelpSys UI surface smoke passed. Startup=$($metrics.startupCoreUiMilliseconds) ms; guide-to-API=$($metrics.guideToLocalApiMilliseconds) ms; size=$($metrics.idleWindowWidth)x$($metrics.idleWindowHeight); status=$($metrics.statusAreaWidth)x$($metrics.statusAreaHeight)."
}
finally {
  Remove-Item Env:HELPSYS_API_BASE -ErrorAction SilentlyContinue
  if ($null -ne $helpSys -and -not $helpSys.HasExited) { Stop-Process -Id $helpSys.Id -Force }
  if ($null -ne $target -and -not $target.HasExited) { Stop-Process -Id $target.Id -Force }
  if ($null -ne $mock -and -not $mock.HasExited) { Stop-Process -Id $mock.Id -Force }
}

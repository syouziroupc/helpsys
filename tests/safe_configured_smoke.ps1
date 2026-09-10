$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

New-Item -ItemType Directory -Force -Path artifacts | Out-Null
$safeExe = Resolve-Path 'smoke-bin/safe/HelpSys.Safe.exe'
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

try {
  Remove-Item 'artifacts/mock-last-request.json' -Force -ErrorAction SilentlyContinue
  $env:HELPSYS_SAFE_API_BASE = 'http://127.0.0.1:8765'
  Remove-Item Env:HELPSYS_SAFE_API_KEY -ErrorAction SilentlyContinue
  Remove-Item Env:HELPSYS_API_BASE -ErrorAction SilentlyContinue
  Remove-Item Env:HELPSYS_API_KEY -ErrorAction SilentlyContinue

  $mock = Start-Process python -ArgumentList 'tests/mock_guide_server.py' -PassThru -WindowStyle Hidden
  Start-Sleep -Seconds 1
  $target = Start-Process powershell.exe -ArgumentList '-NoProfile','-STA','-ExecutionPolicy','Bypass','-File','tests/smoke_target.ps1' -PassThru
  Start-Sleep -Seconds 3
  $helpSys = Start-Process $safeExe -PassThru
  Start-Sleep -Seconds 5
  if ($helpSys.HasExited) { throw 'Configured HelpSys Safe exited during startup.' }

  $request = Find-Element $helpSys 'RequestBox'
  $guide = Find-Element $helpSys 'GuideButton'
  $voice = Find-Element $helpSys 'VoiceButton'
  $commander = Find-Element $helpSys 'CommanderButton'
  $state = Find-Element $helpSys 'StateText'
  if ($null -eq $request -or $null -eq $guide -or $null -eq $voice -or $null -eq $commander -or $null -eq $state) {
    throw 'Configured HelpSys Safe did not expose required UI controls.'
  }
  if ($voice.Current.IsEnabled) { throw 'Configured Safe must still keep cloud Voice disabled.' }
  if ($commander.Current.IsEnabled) { throw 'Configured Safe must still keep Commander cloud voice disabled.' }
  if ($state.Current.Name -notlike '*安全版*') { throw "Configured Safe did not visibly identify the Safe profile. State: $($state.Current.Name)" }

  $request.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('open the test target')
  $guide.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()

  $deadline = [DateTime]::UtcNow.AddSeconds(12)
  while (-not (Test-Path 'artifacts/mock-last-request.json') -and [DateTime]::UtcNow -lt $deadline) {
    if ($helpSys.HasExited) { throw 'Configured HelpSys Safe exited before reaching its approved endpoint.' }
    Start-Sleep -Milliseconds 200
  }

  if (-not (Test-Path 'artifacts/mock-last-request.json')) {
    Save-Screenshot 'helpsys-safe-configured-failure.png'
    throw 'Configured Safe did not reach the explicitly configured approved endpoint.'
  }

  $diagnostics = Get-Content 'artifacts/mock-last-request.json' -Raw -Encoding UTF8 | ConvertFrom-Json
  if ($diagnostics.path -ne '/v1/quality-guide') { throw "Configured Safe used an unexpected route: $($diagnostics.path)" }
  if ($diagnostics.hasScreenshot -ne $true) { throw 'Configured Safe did not send the privacy-approved current screenshot.' }
  if ($diagnostics.eligible.automationId -notcontains 'SmokeButton') { throw 'Configured Safe did not analyze the current safe work surface.' }

  Start-Sleep -Seconds 2
  if ($state.Current.Name -like '*プライバシー保護のため画面解析を一時停止中*') {
    Save-Screenshot 'helpsys-safe-configured-failure.png'
    throw 'Configured Safe incorrectly entered Privacy Mode on the benign smoke surface.'
  }

  Save-Screenshot 'helpsys-safe-configured-guidance.png'
  Write-Host 'HelpSys configured Safe edition E2E smoke passed.'
}
finally {
  Remove-Item Env:HELPSYS_SAFE_API_BASE -ErrorAction SilentlyContinue
  Remove-Item Env:HELPSYS_SAFE_API_KEY -ErrorAction SilentlyContinue
  Remove-Item Env:HELPSYS_API_BASE -ErrorAction SilentlyContinue
  Remove-Item Env:HELPSYS_API_KEY -ErrorAction SilentlyContinue
  if ($null -ne $helpSys -and -not $helpSys.HasExited) { Stop-Process -Id $helpSys.Id -Force }
  if ($null -ne $target -and -not $target.HasExited) { Stop-Process -Id $target.Id -Force }
  if ($null -ne $mock -and -not $mock.HasExited) { Stop-Process -Id $mock.Id -Force }
}

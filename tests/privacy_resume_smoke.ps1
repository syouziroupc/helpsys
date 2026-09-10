$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

New-Item -ItemType Directory -Force -Path artifacts | Out-Null
$exe = Resolve-Path 'smoke-bin/normal/HelpSys.exe'
$mock = $null
$passwordTarget = $null
$safeTarget = $null
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
  $env:HELPSYS_API_BASE = 'http://127.0.0.1:8765'
  $mock = Start-Process python -ArgumentList 'tests/mock_guide_server.py' -PassThru -WindowStyle Hidden
  Start-Sleep -Seconds 1

  $passwordTarget = Start-Process powershell.exe -ArgumentList '-NoProfile','-STA','-ExecutionPolicy','Bypass','-File','tests/privacy_smoke_target.ps1','-Mode','Password' -PassThru
  Start-Sleep -Seconds 3
  $helpSys = Start-Process $exe -PassThru
  Start-Sleep -Seconds 5
  if ($helpSys.HasExited) { throw 'HelpSys exited before Privacy Mode resume smoke.' }

  $request = Find-Element $helpSys 'RequestBox'
  $guide = Find-Element $helpSys 'GuideButton'
  $state = Find-Element $helpSys 'StateText'
  if ($null -eq $request -or $null -eq $guide -or $null -eq $state) { throw 'HelpSys controls missing for Privacy Mode resume smoke.' }

  $request.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('open the test target')
  $guide.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
  Start-Sleep -Seconds 5

  if (Test-Path 'artifacts/mock-last-request.json') { throw 'Cloud request escaped while Password screen was active.' }
  if ($state.Current.Name -notlike '*プライバシー保護のため画面解析を一時停止中*') {
    Save-Screenshot 'helpsys-privacy-resume-block-failure.png'
    throw "HelpSys did not enter Privacy Mode before resume. State: $($state.Current.Name)"
  }
  Save-Screenshot 'helpsys-privacy-resume-blocked.png'

  # Remove the sensitive screen and introduce a known safe work surface. The original request must
  # remain active; no second Guide-button invocation is allowed here.
  Stop-Process -Id $passwordTarget.Id -Force
  $passwordTarget.WaitForExit()
  $passwordTarget = $null
  $safeTarget = Start-Process powershell.exe -ArgumentList '-NoProfile','-STA','-ExecutionPolicy','Bypass','-File','tests/smoke_target.ps1' -PassThru
  Start-Sleep -Seconds 12

  if ($helpSys.HasExited) { throw 'HelpSys exited instead of resuming after leaving Password screen.' }
  if (-not (Test-Path 'artifacts/mock-last-request.json')) {
    Save-Screenshot 'helpsys-privacy-resume-no-request.png'
    throw 'HelpSys did not automatically resume the preserved request after returning to a safe screen.'
  }

  $diagnostics = Get-Content 'artifacts/mock-last-request.json' -Raw -Encoding UTF8 | ConvertFrom-Json
  if ($diagnostics.path -ne '/v1/quality-guide') {
    throw "Privacy Mode resumed through an unexpected route: $($diagnostics.path)"
  }
  if ($diagnostics.hasScreenshot -ne $true) {
    throw 'Resumed safe-screen guidance did not include the safe current screenshot.'
  }
  if ($diagnostics.eligible.automationId -notcontains 'SmokeButton') {
    throw 'Privacy Mode resumed against stale Password-screen structure instead of the new safe work surface.'
  }
  if ($state.Current.Name -like '*プライバシー保護のため画面解析を一時停止中*') {
    Save-Screenshot 'helpsys-privacy-resume-state-stuck.png'
    throw 'HelpSys remained visibly stuck in Privacy Mode after safe-screen cloud planning resumed.'
  }

  Save-Screenshot 'helpsys-privacy-resumed.png'
  Write-Host 'HelpSys Privacy Mode automatic resume E2E smoke passed.'
}
finally {
  Remove-Item Env:HELPSYS_API_BASE -ErrorAction SilentlyContinue
  if ($null -ne $helpSys -and -not $helpSys.HasExited) { Stop-Process -Id $helpSys.Id -Force }
  if ($null -ne $passwordTarget -and -not $passwordTarget.HasExited) { Stop-Process -Id $passwordTarget.Id -Force }
  if ($null -ne $safeTarget -and -not $safeTarget.HasExited) { Stop-Process -Id $safeTarget.Id -Force }
  if ($null -ne $mock -and -not $mock.HasExited) { Stop-Process -Id $mock.Id -Force }
}

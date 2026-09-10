$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient

New-Item -ItemType Directory -Force -Path artifacts | Out-Null
$exe = Resolve-Path 'smoke-bin/normal/HelpSys.exe'
$mock = $null

function Save-DesktopScreenshot([string] $path) {
  $bounds = [System.Windows.Forms.SystemInformation]::VirtualScreen
  $bitmap = New-Object System.Drawing.Bitmap $bounds.Width, $bounds.Height
  $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
  try {
    $graphics.CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)
    $bitmap.Save((Join-Path $PWD $path), [System.Drawing.Imaging.ImageFormat]::Png)
  }
  finally {
    $graphics.Dispose()
    $bitmap.Dispose()
  }
}

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

function Run-PrivacyCase([string]$mode, [string]$expectedStateText, [string]$artifactName) {
  $target = $null
  $helpSys = $null
  try {
    Remove-Item 'artifacts/mock-last-request.json' -Force -ErrorAction SilentlyContinue
    $target = Start-Process powershell.exe -ArgumentList '-NoProfile','-STA','-ExecutionPolicy','Bypass','-File','tests/privacy_smoke_target.ps1','-Mode',$mode -PassThru
    Start-Sleep -Seconds 3

    $helpSys = Start-Process $exe -PassThru
    Start-Sleep -Seconds 5
    if ($helpSys.HasExited) { throw "HelpSys exited during $mode privacy smoke startup." }

    $requestBox = Find-Element $helpSys 'RequestBox'
    $guideButton = Find-Element $helpSys 'GuideButton'
    $stateText = Find-Element $helpSys 'StateText'
    $privacyButton = Find-Element $helpSys 'PrivacyButton'
    if ($null -eq $requestBox -or $null -eq $guideButton -or $null -eq $stateText -or $null -eq $privacyButton) {
      throw "HelpSys privacy UI was not exposed during $mode smoke."
    }

    $value = $requestBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    $value.SetValue('help me continue on this screen')
    $invoke = $guideButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $invoke.Invoke()
    Start-Sleep -Seconds 6

    if ($helpSys.HasExited) { throw "HelpSys exited instead of entering Privacy Mode for $mode." }
    if (Test-Path 'artifacts/mock-last-request.json') {
      Get-Content 'artifacts/mock-last-request.json' -Encoding UTF8
      throw "HelpSys sent a cloud request while the $mode privacy surface was active."
    }

    $state = $stateText.Current.Name
    if ($state -notlike '*プライバシー保護のため画面解析を一時停止中*') {
      Save-DesktopScreenshot "artifacts/$artifactName-failure.png"
      throw "HelpSys did not visibly enter Privacy Mode for $mode. State: $state"
    }
    if ($state -notlike "*$expectedStateText*") {
      Save-DesktopScreenshot "artifacts/$artifactName-failure.png"
      throw "HelpSys Privacy Mode did not expose the expected $mode reason. State: $state"
    }
    if (-not $privacyButton.Current.IsEnabled) {
      throw "Privacy pause/resume control became unavailable during $mode Privacy Mode."
    }

    Save-DesktopScreenshot "artifacts/$artifactName.png"
  }
  finally {
    if ($null -ne $helpSys -and -not $helpSys.HasExited) { Stop-Process -Id $helpSys.Id -Force }
    if ($null -ne $target -and -not $target.HasExited) { Stop-Process -Id $target.Id -Force }
    Start-Sleep -Milliseconds 700
  }
}

try {
  $env:HELPSYS_API_BASE = 'http://127.0.0.1:8765'
  $mock = Start-Process python -ArgumentList 'tests/mock_guide_server.py' -PassThru -WindowStyle Hidden
  Start-Sleep -Seconds 1

  Run-PrivacyCase 'Password' 'パスワード' 'helpsys-privacy-password'
  Run-PrivacyCase 'Otp' '認証コード' 'helpsys-privacy-otp'
  Run-PrivacyCase 'Cookie' 'Cookie・Storage' 'helpsys-privacy-cookie'

  Write-Host 'HelpSys Password/OTP/Cookie GUI privacy smoke passed.'
}
finally {
  Remove-Item Env:HELPSYS_API_BASE -ErrorAction SilentlyContinue
  if ($null -ne $mock -and -not $mock.HasExited) { Stop-Process -Id $mock.Id -Force }
}

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

New-Item -ItemType Directory -Force -Path artifacts | Out-Null
$exe = Resolve-Path 'smoke-bin/normal/HelpSys.exe'
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
  # No cloud call is needed: this is strictly the local user-controlled pause/resume path.
  $target = Start-Process powershell.exe -ArgumentList '-NoProfile','-STA','-ExecutionPolicy','Bypass','-File','tests/smoke_target.ps1' -PassThru
  Start-Sleep -Seconds 3
  $helpSys = Start-Process $exe -PassThru
  Start-Sleep -Seconds 5
  if ($helpSys.HasExited) { throw 'HelpSys exited before manual Privacy control smoke.' }

  $privacy = Find-Element $helpSys 'PrivacyButton'
  $state = Find-Element $helpSys 'StateText'
  if ($null -eq $privacy -or $null -eq $state) { throw 'Manual Privacy controls are not exposed through UI Automation.' }
  if ($privacy.Current.Name -ne '解析停止') { throw "Expected normal Privacy button state before manual pause, got: $($privacy.Current.Name)" }

  $privacy.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
  Start-Sleep -Seconds 1
  if ($helpSys.HasExited) { throw 'HelpSys exited during manual Privacy pause.' }
  if ($privacy.Current.Name -ne '解析再開') { throw "Manual pause must change Privacy button to 解析再開, got: $($privacy.Current.Name)" }
  if ($state.Current.Name -notlike '*利用者の操作で画面解析を停止*') {
    Save-Screenshot 'helpsys-manual-privacy-pause-failure.png'
    throw "Manual pause reason was not visible. State: $($state.Current.Name)"
  }
  Save-Screenshot 'helpsys-manual-privacy-paused.png'

  $privacy.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
  $deadline = [DateTime]::UtcNow.AddSeconds(8)
  do {
    if ($helpSys.HasExited) { throw 'HelpSys exited while resuming manual Privacy pause.' }
    if ($privacy.Current.Name -eq '解析停止' -and $state.Current.Name -like '*画面解析を再開*') { break }
    Start-Sleep -Milliseconds 200
  } while ([DateTime]::UtcNow -lt $deadline)

  if ($privacy.Current.Name -ne '解析停止') {
    Save-Screenshot 'helpsys-manual-privacy-resume-failure.png'
    throw "Manual Privacy resume did not return button to 解析停止. Button: $($privacy.Current.Name)"
  }
  if ($state.Current.Name -notlike '*画面解析を再開*') {
    Save-Screenshot 'helpsys-manual-privacy-resume-failure.png'
    throw "Manual Privacy resume state was not visible. State: $($state.Current.Name)"
  }
  Save-Screenshot 'helpsys-manual-privacy-resumed.png'
  Write-Host 'HelpSys manual Privacy pause/resume GUI smoke passed.'
}
finally {
  if ($null -ne $helpSys -and -not $helpSys.HasExited) { Stop-Process -Id $helpSys.Id -Force }
  if ($null -ne $target -and -not $target.HasExited) { Stop-Process -Id $target.Id -Force }
}

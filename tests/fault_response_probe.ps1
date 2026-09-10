$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path artifacts | Out-Null
$server = Start-Process python -ArgumentList 'tests/fault_guide_server.py' -PassThru -WindowStyle Hidden
$env:HELPSYS_API_BASE = 'http://127.0.0.1:8766'
$target = Start-Process powershell.exe -ArgumentList '-NoProfile','-STA','-ExecutionPolicy','Bypass','-File','tests/smoke_target.ps1' -PassThru
Start-Sleep -Seconds 3
$exe = Resolve-Path 'src/HelpSys.Desktop/bin/Release/net10.0-windows/HelpSys.exe'
Add-Type -AssemblyName UIAutomationClient

function Find-Element([int] $processId, [string] $automationId) {
  $root = [System.Windows.Automation.AutomationElement]::RootElement
  $pc = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $processId)
  $ic = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $automationId)
  return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.AndCondition($pc,$ic)))
}

function Run-FaultCase([string] $mode, [int] $waitSeconds) {
  Set-Content -Path 'artifacts/fault-mode.txt' -Value $mode -Encoding UTF8
  $app = Start-Process $exe -PassThru
  try {
    Start-Sleep -Seconds 4
    if ($app.HasExited) { throw "HelpSys exited before $mode fault case." }
    $box = Find-Element $app.Id 'RequestBox'
    $guide = Find-Element $app.Id 'GuideButton'
    if ($null -eq $box -or $null -eq $guide) { throw "Core UI missing before $mode fault case." }
    $box.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue("open the test target $mode")
    $guide.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Seconds $waitSeconds
    $app.Refresh()
    if ($app.HasExited) { throw "HelpSys crashed during $mode fault case." }
    if (-not $app.Responding) { throw "HelpSys stopped responding during $mode fault case." }
    $boxAfter = Find-Element $app.Id 'RequestBox'
    if ($null -eq $boxAfter) { throw "HelpSys UI disappeared during $mode fault case." }
    Write-Host "FAULT_CASE_${mode}=PASS"
  }
  finally {
    if ($null -ne $app -and -not $app.HasExited) { Stop-Process -Id $app.Id -Force }
  }
}

try {
  Run-FaultCase 'http500' 22
  Run-FaultCase 'invalidjson' 22
  Run-FaultCase 'slow' 25
}
finally {
  if ($null -ne $target -and -not $target.HasExited) { Stop-Process -Id $target.Id -Force }
  if ($null -ne $server -and -not $server.HasExited) { Stop-Process -Id $server.Id -Force }
}

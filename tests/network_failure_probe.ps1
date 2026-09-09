$ErrorActionPreference = 'Stop'
$env:HELPSYS_API_BASE = 'http://127.0.0.1:9'
$target = Start-Process powershell.exe -ArgumentList '-NoProfile','-STA','-ExecutionPolicy','Bypass','-File','tests/smoke_target.ps1' -PassThru
$p = $null
try {
  Start-Sleep -Seconds 2
  $exe = Resolve-Path 'src/HelpSys.Desktop/bin/Release/net10.0-windows/HelpSys.exe'
  $p = Start-Process $exe -PassThru
  Add-Type -AssemblyName UIAutomationClient

  function Find-Element([int] $processId, [string] $automationId) {
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $pc = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $processId)
    $ic = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $automationId)
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.AndCondition($pc,$ic)))
  }

  Start-Sleep -Seconds 4
  if ($p.HasExited) { throw 'HelpSys exited before outage test.' }
  $box = Find-Element $p.Id 'RequestBox'
  $button = Find-Element $p.Id 'GuideButton'
  if ($null -eq $box -or $null -eq $button) { throw 'Core UIA controls missing before outage test.' }
  $box.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('open the test target')
  $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
  Start-Sleep -Seconds 25
  if ($p.HasExited) { throw 'HelpSys crashed while planner API was unavailable.' }
  if ($null -eq (Find-Element $p.Id 'RequestBox')) { throw 'HelpSys UI became unavailable after planner outage.' }
}
finally {
  if ($null -ne $p -and -not $p.HasExited) { Stop-Process -Id $p.Id -Force }
  if (-not $target.HasExited) { Stop-Process -Id $target.Id -Force }
}

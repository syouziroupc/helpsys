$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path artifacts | Out-Null
$mock = Start-Process python -ArgumentList 'tests/mock_guide_server.py' -PassThru -WindowStyle Hidden
$target = $null
$p = $null
try {
  $env:HELPSYS_API_BASE = 'http://127.0.0.1:8765'
  Start-Sleep -Seconds 1
  $target = Start-Process powershell.exe -ArgumentList '-NoProfile','-STA','-ExecutionPolicy','Bypass','-File','tests/smoke_target.ps1' -PassThru
  Start-Sleep -Seconds 2
  $exe = Resolve-Path 'src/HelpSys.Desktop/bin/Release/net10.0-windows/HelpSys.exe'
  Add-Type -AssemblyName UIAutomationClient

  function Find-Element([int] $processId, [string] $automationId) {
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $pc = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $processId)
    $ic = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $automationId)
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.AndCondition($pc,$ic)))
  }

  1..5 | ForEach-Object {
    Write-Host "UI_CYCLE $_"
    $p = Start-Process $exe -PassThru
    Start-Sleep -Seconds 4
    if ($p.HasExited) { throw "HelpSys exited during startup cycle $_." }
    $box = Find-Element $p.Id 'RequestBox'
    $button = Find-Element $p.Id 'GuideButton'
    if ($null -eq $box -or $null -eq $button) { throw "Core UIA controls missing in cycle $_." }
    $box.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('open the test target')
    $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Seconds 7
    if ($p.HasExited) { throw "HelpSys exited during planner cycle $_." }
    if (-not (Test-Path 'artifacts/mock-last-request.json')) { throw "Mock planner was not reached in cycle $_." }
    Stop-Process -Id $p.Id -Force
    $p.WaitForExit()
    $p = $null
    Start-Sleep -Milliseconds 500
  }
}
finally {
  if ($null -ne $p -and -not $p.HasExited) { Stop-Process -Id $p.Id -Force }
  if ($null -ne $target -and -not $target.HasExited) { Stop-Process -Id $target.Id -Force }
  if ($null -ne $mock -and -not $mock.HasExited) { Stop-Process -Id $mock.Id -Force }
}

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path artifacts | Out-Null
Remove-Item 'artifacts/type-text-request-count.txt' -ErrorAction SilentlyContinue

$mock = Start-Process python -ArgumentList 'tests/type_text_mock_server.py' -PassThru -WindowStyle Hidden
$target = $null
$app = $null
try {
  $env:HELPSYS_API_BASE = 'http://127.0.0.1:8767'
  Start-Sleep -Seconds 1
  $target = Start-Process powershell.exe -ArgumentList '-NoProfile','-STA','-ExecutionPolicy','Bypass','-File','tests/advanced_virtual_target.ps1' -PassThru
  Start-Sleep -Seconds 3

  $exe = Resolve-Path 'src/HelpSys.Desktop/bin/Release/net10.0-windows/HelpSys.exe'
  $app = Start-Process $exe -PassThru
  Start-Sleep -Seconds 4
  if ($app.HasExited) { throw 'HelpSys exited before type-text probe.' }

  Add-Type -AssemblyName UIAutomationClient
  Add-Type -AssemblyName System.Windows.Forms

  function Find-Element([int] $processId, [string] $automationId) {
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $pc = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $processId)
    $ic = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $automationId)
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.AndCondition($pc,$ic)))
  }

  function Request-Count {
    if (-not (Test-Path 'artifacts/type-text-request-count.txt')) { return 0 }
    return [int](Get-Content 'artifacts/type-text-request-count.txt' -Raw)
  }

  function Replace-TextWithKeyboard([System.Windows.Automation.AutomationElement] $element, [string] $text) {
    $element.SetFocus()
    Start-Sleep -Milliseconds 180
    [System.Windows.Forms.SendKeys]::SendWait('^a')
    Start-Sleep -Milliseconds 80
    [System.Windows.Forms.SendKeys]::SendWait($text)
    Start-Sleep -Milliseconds 120
  }

  $requestBox = Find-Element $app.Id 'RequestBox'
  $guide = Find-Element $app.Id 'GuideButton'
  if ($null -eq $requestBox -or $null -eq $guide) { throw 'HelpSys core controls missing.' }
  $requestBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('type validation probe')
  $guide.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()

  $deadline = [DateTime]::UtcNow.AddSeconds(20)
  while ((Request-Count) -lt 1 -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 250 }
  if ((Request-Count) -lt 1) { throw 'Initial type_text planner request was not observed.' }
  Start-Sleep -Seconds 3

  $input = Find-Element $target.Id 'ProbeInput'
  if ($null -eq $input) { throw 'ProbeInput was not found.' }

  $baselineWrong = Request-Count
  Replace-TextWithKeyboard $input 'WRONG-TEXT'
  [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
  Start-Sleep -Seconds 4

  $afterWrong = Request-Count
  Write-Host "TYPE_TEXT_REQUESTS_BASELINE_WRONG=$baselineWrong"
  Write-Host "TYPE_TEXT_REQUESTS_AFTER_WRONG=$afterWrong"
  if ($afterWrong -ne $baselineWrong) { throw 'Wrong text caused the planner to advance or replan instead of staying on the current instruction.' }
  if ($app.HasExited) { throw 'HelpSys exited after wrong text validation.' }

  $input = Find-Element $target.Id 'ProbeInput'
  if ($null -eq $input) { throw 'ProbeInput disappeared before correct-input test.' }
  $baselineCorrect = Request-Count
  Replace-TextWithKeyboard $input 'EXPECTED-INPUT-42'
  [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')

  $deadline = [DateTime]::UtcNow.AddSeconds(18)
  while ((Request-Count) -le $baselineCorrect -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 300 }
  $afterCorrect = Request-Count
  Write-Host "TYPE_TEXT_REQUESTS_BASELINE_CORRECT=$baselineCorrect"
  Write-Host "TYPE_TEXT_REQUESTS_AFTER_CORRECT=$afterCorrect"
  if ($afterCorrect -le $baselineCorrect) { throw 'Correct text did not complete the input step and advance planning.' }
  if ($app.HasExited) { throw 'HelpSys exited after correct text validation.' }

  Write-Host 'TYPE_TEXT_LOCAL_VALIDATION=PASS'
}
finally {
  if ($null -ne $app -and -not $app.HasExited) { Stop-Process -Id $app.Id -Force }
  if ($null -ne $target -and -not $target.HasExited) { Stop-Process -Id $target.Id -Force }
  if ($null -ne $mock -and -not $mock.HasExited) { Stop-Process -Id $mock.Id -Force }
}

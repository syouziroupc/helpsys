$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path artifacts | Out-Null
Remove-Item 'artifacts/type-text-request-count.txt' -ErrorAction SilentlyContinue
Remove-Item 'artifacts/type-text-last-request.json' -ErrorAction SilentlyContinue

$targetProject = 'tests/HelpSys.VirtualTarget/HelpSys.VirtualTarget.csproj'
dotnet build $targetProject -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Virtual target build failed.' }
$targetExe = (Resolve-Path 'tests/HelpSys.VirtualTarget/bin/Release/net10.0-windows/HelpSys.VirtualTarget.exe').Path

$mock = Start-Process python -ArgumentList 'tests/type_text_mock_server.py' -PassThru -WindowStyle Hidden
$target = $null
$app = $null
try {
  $env:HELPSYS_API_BASE = 'http://127.0.0.1:8767'
  Start-Sleep -Seconds 1
  $target = Start-Process $targetExe -PassThru

  Add-Type -AssemblyName UIAutomationClient
  Add-Type -AssemblyName System.Windows.Forms

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

  function Request-Count {
    if (-not (Test-Path 'artifacts/type-text-request-count.txt')) { return 0 }
    return [int](Get-Content 'artifacts/type-text-request-count.txt' -Raw)
  }

  function Replace-TextWithKeyboard([System.Windows.Automation.AutomationElement] $element, [string] $text) {
    $element.SetFocus()
    Start-Sleep -Milliseconds 220
    [System.Windows.Forms.SendKeys]::SendWait('^a')
    Start-Sleep -Milliseconds 100
    [System.Windows.Forms.SendKeys]::SendWait($text)
    Start-Sleep -Milliseconds 160
  }

  $input = Wait-Element $target.Id 'ProbeInput'
  if ($null -eq $input) { throw "ProbeInput was not exposed by deterministic target pid=$($target.Id)." }
  Write-Host "TYPE_TEXT_TARGET_PID=$($target.Id)"

  $exe = (Resolve-Path 'src/HelpSys.Desktop/bin/Release/net10.0-windows/HelpSys.exe').Path
  $app = Start-Process $exe -PassThru
  $requestBox = $null
  $guide = $null
  $deadline = [DateTime]::UtcNow.AddSeconds(15)
  while ([DateTime]::UtcNow -lt $deadline) {
    if ($app.HasExited) { throw "HelpSys exited before type-text probe with code $($app.ExitCode)." }
    $requestBox = Find-Element $app.Id 'RequestBox'
    $guide = Find-Element $app.Id 'GuideButton'
    if ($null -ne $requestBox -and $null -ne $guide) { break }
    Start-Sleep -Milliseconds 180
  }
  if ($null -eq $requestBox -or $null -eq $guide) { throw 'HelpSys core controls missing.' }

  $requestBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('type validation probe')
  $guide.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()

  $deadline = [DateTime]::UtcNow.AddSeconds(24)
  while ((Request-Count) -lt 1 -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 200 }
  if ((Request-Count) -lt 1) { throw 'Initial type_text planner request was not observed.' }

  # The first accepted planner request must be grounded on the target itself. A recovery request
  # caused by Windows Terminal or another host is not a valid type_text test setup.
  $last = Get-Content 'artifacts/type-text-last-request.json' -Raw -Encoding UTF8 | ConvertFrom-Json
  $probeInRequest = @($last.elements | Where-Object { $_.automationId -eq 'ProbeInput' }) | Select-Object -First 1
  if ($null -eq $probeInRequest) {
    throw "Initial planner request was not grounded on ProbeInput. foreground=$($last.systemContext.foregroundProcess) title=$($last.systemContext.foregroundTitle)"
  }

  $input = Wait-Element $target.Id 'ProbeInput'
  if ($null -eq $input) { throw 'ProbeInput disappeared before wrong-input test.' }

  $baselineWrong = Request-Count
  Replace-TextWithKeyboard $input 'WRONG-TEXT'
  [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
  Start-Sleep -Seconds 4

  $afterWrong = Request-Count
  Write-Host "TYPE_TEXT_REQUESTS_BASELINE_WRONG=$baselineWrong"
  Write-Host "TYPE_TEXT_REQUESTS_AFTER_WRONG=$afterWrong"
  if ($afterWrong -ne $baselineWrong) { throw 'Wrong text caused planner advance/recovery instead of staying on the current instruction.' }
  if ($app.HasExited) { throw 'HelpSys exited after wrong text validation.' }

  $input = Wait-Element $target.Id 'ProbeInput'
  if ($null -eq $input) { throw 'ProbeInput disappeared before correct-input test.' }
  $baselineCorrect = Request-Count
  Replace-TextWithKeyboard $input 'EXPECTED-INPUT-42'
  [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')

  $deadline = [DateTime]::UtcNow.AddSeconds(22)
  while ((Request-Count) -le $baselineCorrect -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 250 }
  $afterCorrect = Request-Count
  Write-Host "TYPE_TEXT_REQUESTS_BASELINE_CORRECT=$baselineCorrect"
  Write-Host "TYPE_TEXT_REQUESTS_AFTER_CORRECT=$afterCorrect"
  if ($afterCorrect -le $baselineCorrect) { throw 'Correct text did not complete the input step and advance planning.' }
  if ($app.HasExited) { throw 'HelpSys exited after correct text validation.' }

  $last = Get-Content 'artifacts/type-text-last-request.json' -Raw -Encoding UTF8 | ConvertFrom-Json
  $typedHistory = @($last.history | Where-Object { $_.action -eq 'type_text' })
  if ($typedHistory.Count -lt 1) { throw 'Correct type_text completion did not reach planner history.' }

  Write-Host 'TYPE_TEXT_LOCAL_VALIDATION=PASS'
}
finally {
  if ($null -ne $app -and -not $app.HasExited) { Stop-Process -Id $app.Id -Force }
  if ($null -ne $target -and -not $target.HasExited) { Stop-Process -Id $target.Id -Force }
  if ($null -ne $mock -and -not $mock.HasExited) { Stop-Process -Id $mock.Id -Force }
}

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class HelpSysRealClickUser32 {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);
}
'@

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

function Find-TopLevelWindow([System.Diagnostics.Process]$process) {
  $root = [System.Windows.Automation.AutomationElement]::RootElement
  $condition = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
    $process.Id)
  return $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $condition)
}

function Get-ForegroundPid {
  $hwnd = [HelpSysRealClickUser32]::GetForegroundWindow()
  [uint32]$pid = 0
  [void][HelpSysRealClickUser32]::GetWindowThreadProcessId($hwnd, [ref]$pid)
  return [int]$pid
}

function Focus-Process([System.Diagnostics.Process]$process, [string]$label) {
  $deadline = [DateTime]::UtcNow.AddSeconds(8)
  do {
    if ($process.HasExited) { throw "$label exited before foreground focus could be established." }
    $window = Find-TopLevelWindow $process
    if ($null -ne $window) {
      try { $window.SetFocus() } catch { }
      try {
        $handle = [IntPtr]$window.Current.NativeWindowHandle
        if ($handle -ne [IntPtr]::Zero) { [void][HelpSysRealClickUser32]::SetForegroundWindow($handle) }
      } catch { }
    }
    Start-Sleep -Milliseconds 120
    if ((Get-ForegroundPid) -eq $process.Id) { return }
  } while ([DateTime]::UtcNow -lt $deadline)
  throw "Could not make $label the real foreground process."
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

function Physical-LeftClick($rect) {
  if ($rect.IsEmpty) { throw 'Guide button has an empty bounding rectangle.' }
  $x = [int][math]::Round($rect.Left + ($rect.Width / 2.0))
  $y = [int][math]::Round($rect.Top + ($rect.Height / 2.0))
  if (-not [HelpSysRealClickUser32]::SetCursorPos($x, $y)) { throw 'SetCursorPos failed.' }
  Start-Sleep -Milliseconds 80
  [HelpSysRealClickUser32]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
  Start-Sleep -Milliseconds 45
  [HelpSysRealClickUser32]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
}

try {
  Remove-Item 'artifacts/mock-last-request.json' -Force -ErrorAction SilentlyContinue
  $env:HELPSYS_API_BASE = 'http://127.0.0.1:8765'
  $mock = Start-Process python -ArgumentList 'tests/mock_guide_server.py' -PassThru -WindowStyle Hidden
  Start-Sleep -Seconds 1

  $target = Start-Process powershell.exe -ArgumentList '-NoProfile','-STA','-ExecutionPolicy','Bypass','-File','tests/smoke_target.ps1' -PassThru
  Start-Sleep -Seconds 2
  $helpSys = Start-Process $exe -PassThru
  Start-Sleep -Seconds 4
  if ($helpSys.HasExited) { throw 'HelpSys exited before real-click smoke.' }

  $requestBox = Find-Element $helpSys 'RequestBox'
  $guideButton = Find-Element $helpSys 'GuideButton'
  $stateText = Find-Element $helpSys 'StateText'
  if ($null -eq $requestBox -or $null -eq $guideButton -or $null -eq $stateText) {
    throw 'HelpSys UI controls were not available for real-click smoke.'
  }

  $requestBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('open the test target')
  Focus-Process $target 'smoke target'
  $foregroundBefore = Get-ForegroundPid
  if ($foregroundBefore -ne $target.Id) { throw "Foreground was not the target before click. pid=$foregroundBefore target=$($target.Id)" }

  Remove-Item 'artifacts/mock-last-request.json' -Force -ErrorAction SilentlyContinue
  $guideRect = $guideButton.Current.BoundingRectangle
  $timer = [System.Diagnostics.Stopwatch]::StartNew()
  Physical-LeftClick $guideRect

  $focusDeadline = [DateTime]::UtcNow.AddSeconds(2)
  $foregroundAfterClick = 0
  do {
    $foregroundAfterClick = Get-ForegroundPid
    if ($foregroundAfterClick -eq $helpSys.Id) { break }
    Start-Sleep -Milliseconds 30
  } while ([DateTime]::UtcNow -lt $focusDeadline)
  if ($foregroundAfterClick -ne $helpSys.Id) {
    Save-Screenshot 'helpsys-real-click-focus-failure.png'
    throw "Physical click did not reproduce HelpSys taking foreground. pid=$foregroundAfterClick helpsys=$($helpSys.Id)"
  }

  $deadline = [DateTime]::UtcNow.AddSeconds(8)
  while (-not (Test-Path 'artifacts/mock-last-request.json') -and [DateTime]::UtcNow -lt $deadline) {
    if ($helpSys.HasExited) { throw 'HelpSys exited after the physical Guide click.' }
    Start-Sleep -Milliseconds 80
  }
  $timer.Stop()

  if (-not (Test-Path 'artifacts/mock-last-request.json')) {
    Save-Screenshot 'helpsys-real-click-planner-failure.png'
    throw 'A real mouse click on Guide did not reach the local planner within 8 seconds.'
  }

  $diagnostics = Get-Content 'artifacts/mock-last-request.json' -Raw -Encoding UTF8 | ConvertFrom-Json
  if ($diagnostics.path -ne '/v1/quality-guide' -or $diagnostics.hasScreenshot -ne $true) {
    throw 'Real-click smoke reached the wrong planner route or lost its screenshot.'
  }
  if ([int]$diagnostics.foregroundProcessId -ne $target.Id) {
    Save-Screenshot 'helpsys-real-click-wrong-foreground.png'
    throw "Planner analyzed the wrong foreground after a real click. planner=$($diagnostics.foregroundProcessId) target=$($target.Id) helpsys=$($helpSys.Id)"
  }
  if ($diagnostics.foreground -ne 'powershell') {
    throw "Planner foreground process name was unexpected: $($diagnostics.foreground)"
  }
  $helpSysCandidate = @($diagnostics.eligible | Where-Object { $_.processName -eq 'HelpSys' -or $_.processName -eq 'helpsys' })
  if ($helpSysCandidate.Count -gt 0) {
    throw 'HelpSys itself leaked into the planner target candidates after the real mouse click.'
  }

  $stateDeadline = [DateTime]::UtcNow.AddSeconds(5)
  do {
    $stateText = Find-Element $helpSys 'StateText'
    if ($null -ne $stateText -and $stateText.Current.Name -like '手順*') { break }
    Start-Sleep -Milliseconds 100
  } while ([DateTime]::UtcNow -lt $stateDeadline)
  if ($null -eq $stateText -or $stateText.Current.Name -notlike '手順*') {
    Save-Screenshot 'helpsys-real-click-guidance-failure.png'
    throw "Planner request arrived but visible guidance did not start. State=$($stateText.Current.Name)"
  }

  Save-Screenshot 'helpsys-real-click-guidance.png'
  [ordered]@{
    targetPid = $target.Id
    helpSysPid = $helpSys.Id
    foregroundBeforeClickPid = $foregroundBefore
    foregroundAfterPhysicalClickPid = $foregroundAfterClick
    plannerForegroundPid = [int]$diagnostics.foregroundProcessId
    clickToPlannerMilliseconds = [math]::Round($timer.Elapsed.TotalMilliseconds)
    finalState = $stateText.Current.Name
  } | ConvertTo-Json | Set-Content 'artifacts/helpsys-real-click-metrics.json' -Encoding UTF8

  Write-Host "HelpSys real mouse click smoke passed. Click foreground=$foregroundAfterClick; planner foreground=$($diagnostics.foregroundProcessId); latency=$([math]::Round($timer.Elapsed.TotalMilliseconds)) ms."
}
finally {
  Remove-Item Env:HELPSYS_API_BASE -ErrorAction SilentlyContinue
  if ($null -ne $helpSys -and -not $helpSys.HasExited) { Stop-Process -Id $helpSys.Id -Force }
  if ($null -ne $target -and -not $target.HasExited) { Stop-Process -Id $target.Id -Force }
  if ($null -ne $mock -and -not $mock.HasExited) { Stop-Process -Id $mock.Id -Force }
}

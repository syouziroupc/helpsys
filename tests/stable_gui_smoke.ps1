$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class StableSmokeNative {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
  [DllImport("user32.dll")] public static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
}
'@

New-Item -ItemType Directory -Force -Path artifacts | Out-Null
$exe = Resolve-Path 'publish/stable-win-x64/HelpSys.Stable.exe'
$server=$null; $target=$null; $privacy=$null; $app=$null
$log='artifacts/stable-mock-requests.jsonl'

function Find-Element($process,[string]$id) {
  $root=[System.Windows.Automation.AutomationElement]::RootElement
  $p=New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty,$process.Id)
  $a=New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty,$id)
  return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,(New-Object System.Windows.Automation.AndCondition($p,$a)))
}
function Find-Window($process) {
  $root=[System.Windows.Automation.AutomationElement]::RootElement
  $p=New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty,$process.Id)
  return $root.FindFirst([System.Windows.Automation.TreeScope]::Children,$p)
}
function Focus-Process($process,[string]$label) {
  $deadline=[DateTime]::UtcNow.AddSeconds(8)
  do {
    if($process.HasExited){throw "$label exited"}
    $w=Find-Window $process
    if($null-ne $w){
      try{$w.SetFocus()}catch{}
      try{[void][StableSmokeNative]::SetForegroundWindow([IntPtr]$w.Current.NativeWindowHandle)}catch{}
    }
    Start-Sleep -Milliseconds 120
    $h=[StableSmokeNative]::GetForegroundWindow(); [uint32]$foregroundPid=0
    [void][StableSmokeNative]::GetWindowThreadProcessId($h,[ref]$foregroundPid)
    if([int]$foregroundPid -eq $process.Id){return}
  } while([DateTime]::UtcNow -lt $deadline)
  throw "could not foreground $label"
}
function Press-F8 {
  [StableSmokeNative]::keybd_event(0x77,0,0,[UIntPtr]::Zero)
  Start-Sleep -Milliseconds 45
  [StableSmokeNative]::keybd_event(0x77,0,2,[UIntPtr]::Zero)
}
function Wait-Request([int]$seconds=8) {
  $deadline=[DateTime]::UtcNow.AddSeconds($seconds)
  do {
    if(Test-Path $log){ if((Get-Content $log -ErrorAction SilentlyContinue).Count -gt 0){return} }
    if($app.HasExited){throw 'HelpSys Stable exited while waiting for planner request'}
    Start-Sleep -Milliseconds 100
  } while([DateTime]::UtcNow -lt $deadline)
  throw 'Stable planner request did not arrive'
}
function Request-Count {
  if(-not(Test-Path $log)){return 0}
  return @(Get-Content $log | Where-Object { $_.Trim() }).Count
}

try {
  Remove-Item $log -Force -ErrorAction SilentlyContinue
  $env:HELPSYS_STABLE_ENDPOINT='http://127.0.0.1:8766/v1/plan'
  $server=Start-Process python -ArgumentList 'tests/mock_stable_server.py' -PassThru -WindowStyle Hidden
  Start-Sleep -Seconds 1
  $target=Start-Process powershell.exe -ArgumentList '-NoProfile','-STA','-ExecutionPolicy','Bypass','-File','tests/smoke_target.ps1' -PassThru
  Start-Sleep -Seconds 2
  $app=Start-Process $exe -PassThru
  Start-Sleep -Seconds 4
  if($app.HasExited){throw 'HelpSys Stable exited during startup'}

  $goal=Find-Element $app 'GoalBox'
  $guide=Find-Element $app 'GuideButton'
  $status=Find-Element $app 'StatusText'
  $update=Find-Element $app 'UpdateButton'
  $cancel=Find-Element $app 'CancelButton'
  if($null-eq $goal -or $null-eq $guide -or $null-eq $status -or $null-eq $update -or $null-eq $cancel){throw 'Stable core UI controls missing'}
  if(-not $guide.Current.IsEnabled -or -not $update.Current.IsEnabled){throw 'Stable core controls unexpectedly disabled'}

  $goal.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('open the test target')
  Focus-Process $target 'smoke target'
  Press-F8
  Wait-Request
  $req=(Get-Content $log | Select-Object -Last 1 | ConvertFrom-Json)
  if($req.path -ne '/v1/plan' -or -not $req.hasImage){throw 'Stable did not send the expected image planner request'}
  if($req.processName -ne 'powershell'){throw "Stable analyzed wrong process: $($req.processName)"}
  $hit=@($req.controls | Where-Object { $_.automationId -eq 'SmokeButton' -or $_.name -eq 'Open test target' })
  if($hit.Count -lt 1){throw 'Stable UIA scan missed the real target button'}

  $deadline=[DateTime]::UtcNow.AddSeconds(6)
  do {
    $status=Find-Element $app 'StatusText'
    if($null-ne $status -and $status.Current.Name -like '*Open test target*'){break}
    Start-Sleep -Milliseconds 100
  } while([DateTime]::UtcNow -lt $deadline)
  if($null-eq $status -or $status.Current.Name -notlike '*Open test target*'){throw "Stable visible guidance missing: $($status.Current.Name)"}

  Remove-Item $log -Force -ErrorAction SilentlyContinue
  $goal.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('slow double trigger')
  Focus-Process $target 'smoke target'
  Press-F8
  Start-Sleep -Milliseconds 120
  Press-F8
  Start-Sleep -Seconds 3
  if((Request-Count) -ne 1){throw "F8 double trigger caused $(Request-Count) planner requests"}

  Stop-Process -Id $target.Id -Force
  $target=$null
  foreach($mode in @('Password','Cookie')){
    Remove-Item $log -Force -ErrorAction SilentlyContinue
    $privacy=Start-Process powershell.exe -ArgumentList '-NoProfile','-STA','-ExecutionPolicy','Bypass','-File','tests/privacy_smoke_target.ps1','-Mode',$mode -PassThru
    Start-Sleep -Seconds 2
    Focus-Process $privacy "privacy $mode"
    Press-F8
    Start-Sleep -Seconds 3
    if((Request-Count) -ne 0){throw "$mode privacy surface leaked a planner request"}
    $status=Find-Element $app 'StatusText'
    if($null-eq $status){throw "$mode privacy status missing"}
    if($mode -eq 'Password' -and $status.Current.Name -notlike '*パスワード*'){throw "Password block reason missing: $($status.Current.Name)"}
    if($mode -eq 'Cookie' -and $status.Current.Name -notlike '*Cookie*'){throw "Cookie block reason missing: $($status.Current.Name)"}
    Stop-Process -Id $privacy.Id -Force; $privacy=$null
    Start-Sleep -Milliseconds 500
  }

  Write-Host 'HelpSys Stable GUI/F8/privacy/double-trigger smoke passed.'
}
finally {
  Remove-Item Env:HELPSYS_STABLE_ENDPOINT -ErrorAction SilentlyContinue
  foreach($p in @($app,$target,$privacy,$server)){ if($null-ne $p -and -not $p.HasExited){Stop-Process -Id $p.Id -Force} }
}

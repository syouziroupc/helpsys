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
$server=$null; $target=$null; $privacy=$null; $matrix=$null; $stale=$null; $app=$null
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
  $env:HELPSYS_UPDATE_API='http://127.0.0.1:8766/release'
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

  Remove-Item $log -Force -ErrorAction SilentlyContinue
  $matrix=Start-Process powershell.exe -ArgumentList '-NoProfile','-STA','-ExecutionPolicy','Bypass','-File','tests/uia_matrix_target.ps1' -PassThru
  Start-Sleep -Seconds 2
  $goal.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('inspect dense matrix')
  Focus-Process $matrix 'UIA matrix target'
  Press-F8
  Wait-Request
  $req=(Get-Content $log | Select-Object -Last 1 | ConvertFrom-Json)
  if(@($req.controls).Count -gt 240){throw "UIA control cap exceeded: $(@($req.controls).Count)"}
  foreach($expected in @('MatrixActionButton','MatrixEdit','MatrixCheck','MatrixDisabled')){
    if(-not @($req.controls | Where-Object { $_.automationId -eq $expected })){throw "Dense UIA scan missed prioritized control: $expected"}
  }
  $disabled=@($req.controls | Where-Object { $_.automationId -eq 'MatrixDisabled' } | Select-Object -First 1)
  if($disabled.Count -ne 1 -or $disabled[0].enabled -ne $false){throw 'Disabled UIA state was not preserved'}
  Stop-Process -Id $matrix.Id -Force; $matrix=$null

  # Cancellation must abort an in-flight planner call and leave the app reusable.
  Remove-Item $log -Force -ErrorAction SilentlyContinue
  $target=Start-Process powershell.exe -ArgumentList '-NoProfile','-STA','-ExecutionPolicy','Bypass','-File','tests/smoke_target.ps1' -PassThru
  Start-Sleep -Seconds 2
  $goal.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('slow cancellation recovery')
  Focus-Process $target 'cancel target'
  Press-F8
  Wait-Request
  $cancel=Find-Element $app 'CancelButton'
  if($null-eq $cancel){throw 'Cancel button disappeared during planning'}
  $cancel.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
  Start-Sleep -Seconds 2
  if($app.HasExited){throw 'HelpSys exited after cancellation'}
  $status=Find-Element $app 'StatusText'
  if($null-eq $status -or $status.Current.Name -notlike '*中止*'){throw "Cancellation status missing: $($status.Current.Name)"}

  Remove-Item $log -Force -ErrorAction SilentlyContinue
  $goal.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('recovery after cancellation')
  Focus-Process $target 'recovery target'
  Press-F8
  Wait-Request
  Start-Sleep -Seconds 1
  if($app.HasExited){throw 'HelpSys failed to recover after cancellation'}
  Stop-Process -Id $target.Id -Force; $target=$null

  # A target that moves or becomes disabled while Gemini is answering must invalidate guidance.
  foreach($mode in @('move','disable')){
    Remove-Item $log -Force -ErrorAction SilentlyContinue
    $signal=Join-Path $PWD "artifacts/stale-$mode.signal"
    Remove-Item $signal -Force -ErrorAction SilentlyContinue
    $stale=Start-Process powershell.exe -ArgumentList '-NoProfile','-STA','-ExecutionPolicy','Bypass','-File','tests/stale_target.ps1','-SignalPath',$signal -PassThru
    Start-Sleep -Seconds 2
    $goal.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue("slow stale $mode")
    Focus-Process $stale "stale $mode target"
    Press-F8
    Wait-Request
    Set-Content -Path $signal -Value $mode -Encoding ASCII
    Start-Sleep -Seconds 3
    if($app.HasExited){throw "HelpSys exited during stale-$mode revalidation"}
    $status=Find-Element $app 'StatusText'
    if($null-eq $status -or $status.Current.Name -notlike '*古い案内を破棄*'){throw "Stale-$mode guidance was not rejected: $($status.Current.Name)"}
    Stop-Process -Id $stale.Id -Force; $stale=$null
  }

  # Visual-only targets must also be revalidated against the current pixels.
  Remove-Item $log -Force -ErrorAction SilentlyContinue
  $signal=Join-Path $PWD 'artifacts/stale-visual.signal'
  Remove-Item $signal -Force -ErrorAction SilentlyContinue
  $stale=Start-Process powershell.exe -ArgumentList '-NoProfile','-STA','-ExecutionPolicy','Bypass','-File','tests/stale_target.ps1','-SignalPath',$signal -PassThru
  Start-Sleep -Seconds 2
  $goal.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('slow visual stale move')
  Focus-Process $stale 'visual stale target'
  Press-F8
  Wait-Request
  Set-Content -Path $signal -Value 'move' -Encoding ASCII
  Start-Sleep -Seconds 3
  $status=Find-Element $app 'StatusText'
  if($null-eq $status -or $status.Current.Name -notlike '*古い案内を破棄*'){throw "Visual stale guidance was not rejected: $($status.Current.Name)"}
  Stop-Process -Id $stale.Id -Force; $stale=$null

  # Update cancellation must abort the HTTP request and restore the idle UI.
  $update=Find-Element $app 'UpdateButton'
  $cancel=Find-Element $app 'CancelButton'
  if($null-eq $update -or $null-eq $cancel){throw 'Update/cancel controls missing before cancellation test'}
  $update.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
  Start-Sleep -Milliseconds 350
  $cancel.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
  $deadline=[DateTime]::UtcNow.AddSeconds(4)
  do {
    $status=Find-Element $app 'StatusText'
    $update=Find-Element $app 'UpdateButton'
    if($null-ne $status -and $status.Current.Name -like '*更新処理を中止*' -and $update.Current.IsEnabled){break}
    Start-Sleep -Milliseconds 100
  } while([DateTime]::UtcNow -lt $deadline)
  if($null-eq $status -or $status.Current.Name -notlike '*更新処理を中止*'){throw "Update cancellation did not settle: $($status.Current.Name)"}
  if(-not $update.Current.IsEnabled){throw 'Update button did not recover after cancellation'}

  Write-Host 'HelpSys Stable GUI/F8/privacy/double-trigger/dense-UIA/cancel/stale-target/update-cancel smoke passed.'
}
finally {
  Remove-Item Env:HELPSYS_STABLE_ENDPOINT -ErrorAction SilentlyContinue
  Remove-Item Env:HELPSYS_UPDATE_API -ErrorAction SilentlyContinue
  foreach($p in @($app,$target,$privacy,$matrix,$stale,$server)){ if($null-ne $p -and -not $p.HasExited){Stop-Process -Id $p.Id -Force} }
}

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName System.Windows.Forms
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class HelpSysYoutubeUser32 {
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
}
'@

$runnerWindow = [HelpSysYoutubeUser32]::GetForegroundWindow()
if ($runnerWindow -ne [IntPtr]::Zero) {
  [void][HelpSysYoutubeUser32]::ShowWindow($runnerWindow, 6)
  Start-Sleep -Milliseconds 500
}

New-Item -ItemType Directory -Force -Path artifacts | Out-Null
$exe = Resolve-Path 'smoke-bin/normal/HelpSys.exe'
$helpSys = $null
$edge = $null
$steps = @()

function Find-Element([System.Diagnostics.Process]$process, [string]$automationId) {
  $root = [System.Windows.Automation.AutomationElement]::RootElement
  $pc = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $process.Id)
  $ic = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $automationId)
  return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.AndCondition($pc, $ic)))
}

function Get-TopWindows([System.Diagnostics.Process]$process) {
  $root = [System.Windows.Automation.AutomationElement]::RootElement
  $pc = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $process.Id)
  $all = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $pc)
  $result = @()
  foreach ($w in $all) {
    try {
      if (-not $w.Current.IsOffscreen) { $result += $w }
    } catch { }
  }
  return $result
}

function Get-WindowText($window) {
  try {
    $texts = $window.FindAll(
      [System.Windows.Automation.TreeScope]::Descendants,
      (New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Text)))
    return (@($texts | ForEach-Object { $_.Current.Name } | Where-Object { $_ }) -join ' ')
  } catch { return '' }
}

function Get-OverlayRect([System.Diagnostics.Process]$process) {
  foreach ($w in (Get-TopWindows $process)) {
    try {
      $r = $w.Current.BoundingRectangle
      if ($r.IsEmpty -or $r.Width -lt 12 -or $r.Height -lt 12) { continue }
      $name = $w.Current.Name
      $text = Get-WindowText $w
      if ($name -eq 'HelpSys') { continue }
      if ([math]::Abs($r.Width - 380) -lt 8 -and [math]::Abs($r.Height - 76) -lt 8) { continue }
      if ([math]::Abs($r.Width - 520) -lt 12 -and [math]::Abs($r.Height - 170) -lt 12) { continue }
      if ([string]::IsNullOrWhiteSpace($text)) { return $r }
    } catch { }
  }
  return $null
}

function Get-Instruction([System.Diagnostics.Process]$process) {
  foreach ($w in (Get-TopWindows $process)) {
    try {
      $r = $w.Current.BoundingRectangle
      if ([math]::Abs($r.Width - 380) -lt 12 -and [math]::Abs($r.Height - 76) -lt 12) {
        return Get-WindowText $w
      }
    } catch { }
  }
  return ''
}

function Get-KeyHint([System.Diagnostics.Process]$process) {
  foreach ($w in (Get-TopWindows $process)) {
    try {
      $r = $w.Current.BoundingRectangle
      if ([math]::Abs($r.Width - 520) -lt 15 -and [math]::Abs($r.Height - 170) -lt 15) {
        return Get-WindowText $w
      }
    } catch { }
  }
  return ''
}

function Click-Rect($rect, [bool]$double = $false) {
  $x = [int][math]::Round($rect.Left + $rect.Width / 2)
  $y = [int][math]::Round($rect.Top + $rect.Height / 2)
  if (-not [HelpSysYoutubeUser32]::SetCursorPos($x, $y)) { throw 'SetCursorPos failed.' }
  Start-Sleep -Milliseconds 120
  $count = if ($double) { 2 } else { 1 }
  for ($i=0; $i -lt $count; $i++) {
    [HelpSysYoutubeUser32]::mouse_event(0x0002,0,0,0,[UIntPtr]::Zero)
    Start-Sleep -Milliseconds 45
    [HelpSysYoutubeUser32]::mouse_event(0x0004,0,0,0,[UIntPtr]::Zero)
    if ($double) { Start-Sleep -Milliseconds 90 }
  }
}

function Send-KeyHint([string]$hint) {
  $h = $hint.ToLowerInvariant()
  if ($h -match 'ctrl' -and $h -match '\bt\b') { [System.Windows.Forms.SendKeys]::SendWait('^t'); return 'Ctrl+T' }
  if ($h -match 'ctrl' -and $h -match '\bl\b') { [System.Windows.Forms.SendKeys]::SendWait('^l'); return 'Ctrl+L' }
  if ($h -match 'alt' -and ($h -match 'left' -or $h -match '←')) { [System.Windows.Forms.SendKeys]::SendWait('%{LEFT}'); return 'Alt+Left' }
  if ($h -match 'enter') { [System.Windows.Forms.SendKeys]::SendWait('{ENTER}'); return 'Enter' }
  if ($h -match 'windows' -or $h -match '⊞') {
    Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices;
public static class KeyPressWin {
 [DllImport("user32.dll")] public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
}
'@ -ErrorAction SilentlyContinue
    [KeyPressWin]::keybd_event(0x5B,0,0,[UIntPtr]::Zero)
    [KeyPressWin]::keybd_event(0x5B,0,2,[UIntPtr]::Zero)
    return 'Windows'
  }
  return $null
}

function Browser-ReachedYoutube {
  if ($null -eq $edge -or $edge.HasExited) { return $false }
  try {
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $pc = New-Object System.Windows.Automation.PropertyCondition(
      [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
      $edge.Id)
    $typeCondition = New-Object System.Windows.Automation.PropertyCondition(
      [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
      [System.Windows.Automation.ControlType]::Edit)
    $condition = New-Object System.Windows.Automation.AndCondition($pc, $typeCondition)
    $edits = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)
    foreach ($e in $edits) {
      try {
        $pattern = $null
        if ($e.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$pattern)) {
          $value = ([System.Windows.Automation.ValuePattern]$pattern).Current.Value
          if ($value -match 'youtube\.com') { return $true }
        }
      } catch { }
    }
  } catch { }
  try { if ($edge.MainWindowTitle -match 'YouTube') { return $true } } catch { }
  return $false
}

try {
  $env:HELPSYS_TEST_DIAGNOSTICS = '1'
  $apiBase = $env:HELPSYS_API_BASE
  if ([string]::IsNullOrWhiteSpace($apiBase)) { throw 'HELPSYS_API_BASE was not provided to the YouTube E2E step.' }

  $health = $null
  try {
    $health = Invoke-RestMethod -Method Get -Uri ($apiBase.TrimEnd('/') + '/health') -TimeoutSec 20
  }
  catch {
    [ordered]@{
      apiBase = $apiBase
      healthError = $_.Exception.Message
    } | ConvertTo-Json -Depth 6 | Set-Content artifacts/helpsys-youtube-api-preflight.json -Encoding UTF8
    throw
  }

  [ordered]@{
    apiBase = $apiBase
    health = $health
  } | ConvertTo-Json -Depth 8 | Set-Content artifacts/helpsys-youtube-api-preflight.json -Encoding UTF8

  $browserCandidates = @(
    @{ name='msedge'; path="$env:ProgramFiles(x86)\Microsoft\Edge\Application\msedge.exe"; args=@('--new-window','about:blank','--no-first-run','--disable-features=msEdgeFirstRunExperience') },
    @{ name='msedge'; path="$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe"; args=@('--new-window','about:blank','--no-first-run','--disable-features=msEdgeFirstRunExperience') },
    @{ name='chrome'; path="$env:ProgramFiles\Google\Chrome\Application\chrome.exe"; args=@('--new-window','about:blank','--no-first-run','--disable-default-apps') },
    @{ name='chrome'; path="$env:ProgramFiles(x86)\Google\Chrome\Application\chrome.exe"; args=@('--new-window','about:blank','--no-first-run','--disable-default-apps') },
    @{ name='firefox'; path="$env:ProgramFiles\Mozilla Firefox\firefox.exe"; args=@('-new-window','about:blank') },
    @{ name='firefox'; path="$env:ProgramFiles(x86)\Mozilla Firefox\firefox.exe"; args=@('-new-window','about:blank') }
  )
  $browser = $browserCandidates | Where-Object { Test-Path $_.path } | Select-Object -First 1
  if (-not $browser) {
    $edgeCommand = Get-Command msedge.exe -ErrorAction SilentlyContinue
    $chromeCommand = Get-Command chrome.exe -ErrorAction SilentlyContinue
    if ($edgeCommand) { $browser = @{ name='msedge'; path=$edgeCommand.Source; args=@('--new-window','about:blank','--no-first-run') } }
    elseif ($chromeCommand) { $browser = @{ name='chrome'; path=$chromeCommand.Source; args=@('--new-window','about:blank','--no-first-run') } }
    else {
      $firefoxCommand = Get-Command firefox.exe -ErrorAction SilentlyContinue
      if ($firefoxCommand) { $browser = @{ name='firefox'; path=$firefoxCommand.Source; args=@('-new-window','about:blank') } }
    }
  }
  if (-not $browser) { throw 'No supported browser (Edge/Chrome/Firefox) was found on the Windows runner.' }

  Get-Process $browser.name -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
  Start-Sleep -Seconds 1
  [void](Start-Process $browser.path -ArgumentList $browser.args -PassThru)

  $browserDeadline = [DateTime]::UtcNow.AddSeconds(15)
  do {
    Start-Sleep -Milliseconds 300
    $edge = Get-Process $browser.name -ErrorAction SilentlyContinue |
      Where-Object { $_.MainWindowHandle -ne 0 } |
      Sort-Object StartTime -Descending |
      Select-Object -First 1
    if ($null -ne $edge) { break }
  } while ([DateTime]::UtcNow -lt $browserDeadline)

  if ($null -eq $edge) {
    $shortcutNames = switch ($browser.name) {
      'chrome' { @('Google Chrome*.lnk','Chrome*.lnk') }
      'msedge' { @('Microsoft Edge*.lnk','Edge*.lnk') }
      'firefox' { @('Firefox*.lnk','Mozilla Firefox*.lnk') }
      default { @() }
    }
    $shortcut = $null
    foreach ($pattern in $shortcutNames) {
      $shortcut = Get-ChildItem "$env:PUBLIC\Desktop","$env:USERPROFILE\Desktop" -Filter $pattern -ErrorAction SilentlyContinue | Select-Object -First 1
      if ($shortcut) { break }
    }
    if ($shortcut) {
      Start-Process $shortcut.FullName
      $browserDeadline = [DateTime]::UtcNow.AddSeconds(15)
      do {
        Start-Sleep -Milliseconds 300
        $edge = Get-Process $browser.name -ErrorAction SilentlyContinue |
          Where-Object { $_.MainWindowHandle -ne 0 } |
          Sort-Object StartTime -Descending |
          Select-Object -First 1
        if ($null -ne $edge) { break }
      } while ([DateTime]::UtcNow -lt $browserDeadline)
    }
  }

  if ($null -eq $edge) { throw "$($browser.name) did not expose a top-level window after direct and shortcut launch attempts." }
  [void][HelpSysYoutubeUser32]::SetForegroundWindow([IntPtr]$edge.MainWindowHandle)
  Start-Sleep -Seconds 1

  $helpSys = Start-Process $exe -PassThru
  Start-Sleep -Seconds 5
  if ($helpSys.HasExited) { throw 'HelpSys exited before YouTube scenario.' }

  $request = Find-Element $helpSys 'RequestBox'
  $guide = Find-Element $helpSys 'GuideButton'
  $state = Find-Element $helpSys 'StateText'
  if ($null -eq $request -or $null -eq $guide) { throw 'HelpSys input controls are missing.' }

  $request.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('YouTubeを見たい')
  $guideRect = $guide.Current.BoundingRectangle
  Click-Rect $guideRect

  $deadline = [DateTime]::UtcNow.AddSeconds(95)
  $lastSignature = ''
  $stagnant = 0

  while ([DateTime]::UtcNow -lt $deadline) {
    Start-Sleep -Milliseconds 500
    if ($helpSys.HasExited) { throw 'HelpSys exited during YouTube scenario.' }
    if (Browser-ReachedYoutube) {
      $steps += [ordered]@{ time=[DateTime]::UtcNow.ToString('o'); action='reached'; detail='youtube.com' }
      break
    }

    $keyHint = Get-KeyHint $helpSys
    $instruction = Get-Instruction $helpSys
    $overlay = Get-OverlayRect $helpSys
    $stateValue = if ($null -ne $state) { $state.Current.Name } else { '' }
    $sig = "$keyHint|$instruction|$($overlay.Left),$($overlay.Top),$($overlay.Width),$($overlay.Height)|$stateValue"

    if ($sig -eq $lastSignature) { $stagnant++ } else { $stagnant = 0; $lastSignature = $sig }
    if ($stagnant -gt 18) {
      $steps += [ordered]@{ time=[DateTime]::UtcNow.ToString('o'); action='stalled'; detail=$sig }
      break
    }

    if (-not [string]::IsNullOrWhiteSpace($keyHint)) {
      $sent = Send-KeyHint $keyHint
      if ($sent) {
        $steps += [ordered]@{ time=[DateTime]::UtcNow.ToString('o'); action='key'; detail=$sent; instruction=$keyHint }
        Start-Sleep -Seconds 2
        continue
      }
    }

    if ($null -ne $overlay) {
      $text = $instruction
      if ($text -match '入力' -and ($text -match 'YouTube' -or $text -match 'youtube')) {
        [System.Windows.Forms.SendKeys]::SendWait('YouTube')
        Start-Sleep -Milliseconds 250
        [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
        $steps += [ordered]@{ time=[DateTime]::UtcNow.ToString('o'); action='type'; detail='YouTube + Enter'; instruction=$text }
        Start-Sleep -Seconds 3
        continue
      }

      $double = $text -match '2回'
      Click-Rect $overlay $double
      $steps += [ordered]@{ time=[DateTime]::UtcNow.ToString('o'); action=($(if($double){'double_click'}else{'click'})); detail="$([math]::Round($overlay.Left)),$([math]::Round($overlay.Top))"; instruction=$text }
      Start-Sleep -Seconds 2
      continue
    }
  }

  $reached = Browser-ReachedYoutube
  [ordered]@{
    reachedYoutube = $reached
    finalEdgeTitle = $(try { $edge.MainWindowTitle } catch { '' })
    finalHelpSysState = $(if ($null -ne $state) { $state.Current.Name } else { '' })
    steps = $steps
  } | ConvertTo-Json -Depth 6 | Set-Content artifacts/helpsys-youtube-e2e.json -Encoding UTF8

  if (-not $reached) { throw 'HelpSys did not reach YouTube within the end-to-end scenario window.' }
  Write-Host "HelpSys YouTube end-to-end scenario reached YouTube in $($steps.Count) observed actions."
}
finally {
  Remove-Item Env:HELPSYS_TEST_DIAGNOSTICS -ErrorAction SilentlyContinue
  if ($null -ne $helpSys -and -not $helpSys.HasExited) { Stop-Process -Id $helpSys.Id -Force -ErrorAction SilentlyContinue }
  Get-Process msedge,chrome,firefox -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
}

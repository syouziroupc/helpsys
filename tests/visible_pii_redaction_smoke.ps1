$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

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

function Save-DesktopScreenshot([string]$name) {
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

function Assert-HasSolidRedaction([string]$path) {
  $resolvedPath = (Resolve-Path $path).Path
  $bitmap = [System.Drawing.Bitmap]::new($resolvedPath)
  try {
    $rowsWithLongBlackRun = 0
    $maxRun = 0
    for ($y = 0; $y -lt $bitmap.Height; $y++) {
      $run = 0
      $rowMax = 0
      for ($x = 0; $x -lt $bitmap.Width; $x++) {
        $pixel = $bitmap.GetPixel($x, $y)
        if ($pixel.R -le 8 -and $pixel.G -le 8 -and $pixel.B -le 8) {
          $run++
          if ($run -gt $rowMax) { $rowMax = $run }
        }
        else {
          $run = 0
        }
      }
      if ($rowMax -gt $maxRun) { $maxRun = $rowMax }
      if ($rowMax -ge 80) { $rowsWithLongBlackRun++ }
    }

    if ($maxRun -lt 80 -or $rowsWithLongBlackRun -lt 8) {
      throw "Outbound screenshot does not contain a convincing solid local redaction block. maxRun=$maxRun rows=$rowsWithLongBlackRun"
    }
  }
  finally {
    $bitmap.Dispose()
  }
}

try {
  Remove-Item 'artifacts/mock-last-request.json' -Force -ErrorAction SilentlyContinue
  Remove-Item 'artifacts/helpsys-pii-egress-image.png' -Force -ErrorAction SilentlyContinue
  $env:HELPSYS_API_BASE = 'http://127.0.0.1:8765'

  $mock = Start-Process python -ArgumentList 'tests/mock_guide_server.py' -PassThru -WindowStyle Hidden
  Start-Sleep -Seconds 1
  $target = Start-Process powershell.exe -ArgumentList '-NoProfile','-STA','-ExecutionPolicy','Bypass','-File','tests/pii_smoke_target.ps1' -PassThru
  Start-Sleep -Seconds 3
  Save-DesktopScreenshot 'helpsys-pii-source.png'

  $helpSys = Start-Process $exe -PassThru
  Start-Sleep -Seconds 5
  if ($helpSys.HasExited) { throw 'HelpSys exited before visible PII redaction smoke.' }

  $request = Find-Element $helpSys 'RequestBox'
  $guide = Find-Element $helpSys 'GuideButton'
  if ($null -eq $request -or $null -eq $guide) { throw 'HelpSys controls missing during visible PII redaction smoke.' }

  $request.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('PII redaction smoke: open the test target')
  $guide.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()

  $deadline = [DateTime]::UtcNow.AddSeconds(12)
  while (-not (Test-Path 'artifacts/helpsys-pii-egress-image.png') -and [DateTime]::UtcNow -lt $deadline) {
    if ($helpSys.HasExited) { throw 'HelpSys exited before producing the outbound redacted screenshot.' }
    Start-Sleep -Milliseconds 200
  }

  if (-not (Test-Path 'artifacts/helpsys-pii-egress-image.png')) {
    Save-DesktopScreenshot 'helpsys-pii-redaction-failure.png'
    throw 'Mock endpoint did not receive the exact outbound PII smoke screenshot.'
  }
  if (-not (Test-Path 'artifacts/mock-last-request.json')) { throw 'PII redaction smoke diagnostics are missing.' }

  $diagnosticsRaw = Get-Content 'artifacts/mock-last-request.json' -Raw -Encoding UTF8
  foreach ($forbidden in @('alice@example.com', '090-1234-5678', '123-4567')) {
    if ($diagnosticsRaw.Contains($forbidden, [StringComparison]::OrdinalIgnoreCase)) {
      throw "Raw visible PII leaked into the outbound structured diagnostics: $forbidden"
    }
  }

  Assert-HasSolidRedaction 'artifacts/helpsys-pii-egress-image.png'
  Write-Host 'HelpSys exact outbound visible-PII screenshot redaction smoke passed.'
}
finally {
  Remove-Item Env:HELPSYS_API_BASE -ErrorAction SilentlyContinue
  if ($null -ne $helpSys -and -not $helpSys.HasExited) { Stop-Process -Id $helpSys.Id -Force }
  if ($null -ne $target -and -not $target.HasExited) { Stop-Process -Id $target.Id -Force }
  if ($null -ne $mock -and -not $mock.HasExited) { Stop-Process -Id $mock.Id -Force }
}

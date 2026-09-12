$ErrorActionPreference = 'Stop'

$path = 'artifacts/mock-last-request.json'
if (-not (Test-Path $path)) { throw 'Real-click planner diagnostics are missing.' }

$diagnostics = Get-Content $path -Raw -Encoding UTF8 | ConvertFrom-Json
$eligible = @($diagnostics.eligible)
if ($eligible.Count -eq 0) { throw 'Planner received no eligible UI candidates.' }

$windowChrome = @($eligible | Where-Object {
  $name = [string]$_.name
  $automationId = [string]$_.automationId
  ($_.controlType -eq 'Button' -and $automationId -in @('Minimize','Maximize','Restore','Close')) -or
  ($_.controlType -eq 'MenuItem' -and $name -in @('System','システム','システム メニュー'))
})

if ($windowChrome.Count -gt 0) {
  $summary = ($windowChrome | ForEach-Object { "$($_.controlType):$($_.name)/$($_.automationId)" }) -join ', '
  throw "Normal guidance leaked non-client window chrome into planner candidates: $summary"
}

if (-not ($eligible | Where-Object { $_.automationId -eq 'SmokeButton' -and $_.name -eq 'Open test target' })) {
  throw 'Candidate filtering removed the real application action along with window chrome.'
}

Write-Host "Real-click candidate filtering passed. Eligible count=$($eligible.Count); window chrome=0."

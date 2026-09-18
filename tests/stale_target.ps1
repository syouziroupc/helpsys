param(
  [Parameter(Mandatory=$true)][string]$SignalPath
)
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$form = New-Object System.Windows.Forms.Form
$form.Text = 'HelpSys Stale Target'
$form.Width = 640
$form.Height = 360
$form.StartPosition = 'CenterScreen'
$form.TopMost = $true

$label = New-Object System.Windows.Forms.Label
$label.Text = 'Target must remain stable while planner is answering'
$label.AutoSize = $true
$label.Left = 28
$label.Top = 28

$button = New-Object System.Windows.Forms.Button
$button.Name = 'StaleButton'
$button.Text = 'Stale action'
$button.Left = 28
$button.Top = 100
$button.Width = 160
$button.Height = 36

$form.Controls.Add($label)
$form.Controls.Add($button)

$timer = New-Object System.Windows.Forms.Timer
$timer.Interval = 80
$timer.Add_Tick({
  if(Test-Path $SignalPath){
    $cmd=(Get-Content $SignalPath -Raw -ErrorAction SilentlyContinue).Trim()
    if($cmd -eq 'move'){
      $button.Left = 360
      $label.Text = 'Target moved'
      $button.Refresh()
      $label.Refresh()
      $form.Refresh()
      [System.Windows.Forms.Application]::DoEvents()
      Set-Content -Path ($SignalPath + '.ack') -Value 'moved' -Encoding ASCII
      Remove-Item $SignalPath -Force -ErrorAction SilentlyContinue
    } elseif($cmd -eq 'disable'){
      $button.Enabled = $false
      $label.Text = 'Target disabled'
      $button.Refresh()
      $label.Refresh()
      $form.Refresh()
      [System.Windows.Forms.Application]::DoEvents()
      Set-Content -Path ($SignalPath + '.ack') -Value 'disabled' -Encoding ASCII
      Remove-Item $SignalPath -Force -ErrorAction SilentlyContinue
    }
  }
})
$form.Add_Shown({ $form.Activate(); $timer.Start() })
$form.Add_FormClosed({ $timer.Stop(); $timer.Dispose() })
[System.Windows.Forms.Application]::Run($form)

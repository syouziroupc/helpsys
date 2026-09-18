param()
Add-Type -AssemblyName PresentationFramework
Add-Type -AssemblyName UIAutomationTypes

$window = New-Object System.Windows.Window
$window.Title = 'HelpSys UIA Matrix Target'
$window.Width = 900
$window.Height = 700
$window.WindowStartupLocation = 'CenterScreen'

$canvas = New-Object System.Windows.Controls.Canvas
$window.Content = $canvas

for($i=0; $i -lt 280; $i++){
  $t = New-Object System.Windows.Controls.TextBlock
  $t.Text = "filler-$i"
  $t.FontSize = 1
  $t.Width = 80
  $t.Height = 3
  [System.Windows.Controls.Canvas]::SetLeft($t, 4 + (($i % 14) * 2))
  [System.Windows.Controls.Canvas]::SetTop($t, 4 + (($i % 20) * 2))
  $canvas.Children.Add($t) | Out-Null
}

$button = New-Object System.Windows.Controls.Button
$button.Content = 'Matrix Action'
$button.Width = 160
$button.Height = 42
[System.Windows.Automation.AutomationProperties]::SetAutomationId($button,'MatrixActionButton')
[System.Windows.Controls.Canvas]::SetLeft($button, 40)
[System.Windows.Controls.Canvas]::SetTop($button, 100)
$canvas.Children.Add($button) | Out-Null

$edit = New-Object System.Windows.Controls.TextBox
$edit.Text = 'matrix'
$edit.Width = 220
$edit.Height = 32
[System.Windows.Automation.AutomationProperties]::SetAutomationId($edit,'MatrixEdit')
[System.Windows.Controls.Canvas]::SetLeft($edit, 40)
[System.Windows.Controls.Canvas]::SetTop($edit, 160)
$canvas.Children.Add($edit) | Out-Null

$check = New-Object System.Windows.Controls.CheckBox
$check.Content = 'Matrix Check'
[System.Windows.Automation.AutomationProperties]::SetAutomationId($check,'MatrixCheck')
[System.Windows.Controls.Canvas]::SetLeft($check, 40)
[System.Windows.Controls.Canvas]::SetTop($check, 220)
$canvas.Children.Add($check) | Out-Null

$disabled = New-Object System.Windows.Controls.Button
$disabled.Content = 'Matrix Disabled'
$disabled.IsEnabled = $false
$disabled.Width = 160
$disabled.Height = 42
[System.Windows.Automation.AutomationProperties]::SetAutomationId($disabled,'MatrixDisabled')
[System.Windows.Controls.Canvas]::SetLeft($disabled, 40)
[System.Windows.Controls.Canvas]::SetTop($disabled, 270)
$canvas.Children.Add($disabled) | Out-Null

$window.ShowDialog() | Out-Null

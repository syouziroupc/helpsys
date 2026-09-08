Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$form = New-Object System.Windows.Forms.Form
$form.Text = 'HelpSys Smoke Target'
$form.Width = 640
$form.Height = 360
$form.StartPosition = 'CenterScreen'

$label = New-Object System.Windows.Forms.Label
$label.Text = 'HelpSys smoke target'
$label.AutoSize = $true
$label.Left = 28
$label.Top = 28

$input = New-Object System.Windows.Forms.TextBox
$input.Name = 'SmokeInput'
$input.Text = ''
$input.Left = 28
$input.Top = 70
$input.Width = 360

$button = New-Object System.Windows.Forms.Button
$button.Name = 'SmokeButton'
$button.Text = 'Open test target'
$button.Left = 28
$button.Top = 120
$button.Width = 150
$button.Height = 34
$button.Add_Click({ $label.Text = 'Button clicked' })

$form.Controls.Add($label)
$form.Controls.Add($input)
$form.Controls.Add($button)
[System.Windows.Forms.Application]::Run($form)

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$form = New-Object System.Windows.Forms.Form
$form.Text = 'HelpSys Smoke Target'
$form.Width = 640
$form.Height = 360
$form.StartPosition = 'CenterScreen'
# GitHub's Windows Server runner may host powershell.exe inside Windows Terminal, so
# process/title activation is not reliable. Keep the dedicated test form topmost; HelpSys
# is also topmost and therefore sits above it. SystemContextService must skip HelpSys itself
# and resolve this real work surface directly beneath it, which matches the production case.
$form.TopMost = $true

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
$form.Add_Shown({ $form.Activate() })
[System.Windows.Forms.Application]::Run($form)

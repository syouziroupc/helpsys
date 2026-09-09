Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$form = New-Object System.Windows.Forms.Form
$form.Text = 'HelpSys Advanced Virtual Target'
$form.Width = 900
$form.Height = 450
$form.StartPosition = 'Manual'
$form.Left = 40
$form.Top = 220
$form.TopMost = $true

$label = New-Object System.Windows.Forms.Label
$label.Text = 'Advanced HelpSys test surface'
$label.AutoSize = $true
$label.Left = 36
$label.Top = 28

$input = New-Object System.Windows.Forms.TextBox
$input.Name = 'ProbeInput'
$input.Text = 'PRIVATE-PROBE-847251'
$input.Left = 36
$input.Top = 72
$input.Width = 430

$button = New-Object System.Windows.Forms.Button
$button.Name = 'SmokeButton'
$button.Text = 'Open test target'
$button.Left = 36
$button.Top = 128
$button.Width = 170
$button.Height = 38
$button.Add_Click({ $label.Text = 'Correct target clicked' })

$wrong = New-Object System.Windows.Forms.Button
$wrong.Name = 'WrongButton'
$wrong.Text = 'Wrong path'
$wrong.Left = 650
$wrong.Top = 250
$wrong.Width = 160
$wrong.Height = 42
$wrong.Add_Click({ $label.Text = 'Wrong path clicked' })

$form.Controls.Add($label)
$form.Controls.Add($input)
$form.Controls.Add($button)
$form.Controls.Add($wrong)
$form.Add_Shown({ $form.Activate() })
[System.Windows.Forms.Application]::Run($form)

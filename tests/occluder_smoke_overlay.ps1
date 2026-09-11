$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System.Windows.Forms;

public sealed class HelpSysNoActivateOverlay : Form
{
    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_NOACTIVATE = 0x08000000;
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_NOACTIVATE;
            return cp;
        }
    }
}
'@ -ReferencedAssemblies System.Windows.Forms.dll,System.Drawing.dll

$form = New-Object HelpSysNoActivateOverlay
$form.Text = 'Unrelated Overlay Smoke'
$form.Width = 340
$form.Height = 130
$form.StartPosition = 'CenterScreen'
$form.TopMost = $true
$form.ShowInTaskbar = $false
$form.FormBorderStyle = [System.Windows.Forms.FormBorderStyle]::FixedToolWindow
$form.BackColor = [System.Drawing.Color]::White

$label = New-Object System.Windows.Forms.Label
$label.Text = 'UNRELATED OVERLAY - MUST BE REDACTED'
$label.AutoSize = $false
$label.TextAlign = [System.Drawing.ContentAlignment]::MiddleCenter
$label.Dock = [System.Windows.Forms.DockStyle]::Fill
$label.Font = New-Object System.Drawing.Font('Segoe UI', 13, [System.Drawing.FontStyle]::Bold)
$form.Controls.Add($label)

$form.Add_Shown({
    New-Item -ItemType Directory -Force -Path artifacts | Out-Null
    [pscustomobject]@{
        left = $form.Left
        top = $form.Top
        width = $form.Width
        height = $form.Height
        processId = $PID
    } | ConvertTo-Json -Compress | Set-Content -Path 'artifacts/occluder-overlay-bounds.json' -Encoding UTF8
})

[System.Windows.Forms.Application]::Run($form)

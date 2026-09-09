using System.Drawing;
using System.Windows.Forms;

namespace HelpSys.VirtualTarget;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new VirtualTargetForm());
    }
}

internal sealed class VirtualTargetForm : Form
{
    private readonly Label _statusLabel;

    public VirtualTargetForm()
    {
        Name = "VirtualTargetWindow";
        Text = "PRIVATE-WINDOW-TITLE-319751";
        Width = 920;
        Height = 500;
        StartPosition = FormStartPosition.Manual;
        Location = new Point(40, 220);
        TopMost = true;

        var heading = new Label
        {
            Name = "HeadingLabel",
            Text = "Advanced HelpSys test surface",
            AutoSize = true,
            Left = 36,
            Top = 24
        };

        var input = new TextBox
        {
            Name = "ProbeInput",
            Text = "PRIVATE-PROBE-847251",
            Left = 36,
            Top = 68,
            Width = 430
        };

        var smokeButton = new Button
        {
            Name = "SmokeButton",
            Text = "Open test target",
            Left = 36,
            Top = 124,
            Width = 170,
            Height = 38
        };
        smokeButton.Click += (_, _) => _statusLabel.Text = "Correct target clicked";

        var toggle = new CheckBox
        {
            Name = "ProbeToggle",
            Text = "Probe toggle",
            Left = 36,
            Top = 184,
            Width = 180
        };

        var modalButton = new Button
        {
            Name = "OpenModalButton",
            Text = "Open modal",
            Left = 236,
            Top = 184,
            Width = 150,
            Height = 38
        };
        modalButton.Click += (_, _) => BeginInvoke(new Action(ShowProbeModal));

        var titleButton = new Button
        {
            Name = "ChangeTitleButton",
            Text = "Change title only",
            Left = 416,
            Top = 184,
            Width = 170,
            Height = 38
        };
        titleButton.Click += (_, _) => Text = Text.EndsWith("-ALT", StringComparison.Ordinal)
            ? "PRIVATE-WINDOW-TITLE-319751"
            : "PRIVATE-WINDOW-TITLE-319751-ALT";

        var wrong = new Button
        {
            Name = "WrongButton",
            Text = "Wrong path",
            Left = 680,
            Top = 286,
            Width = 160,
            Height = 42
        };
        wrong.Click += (_, _) => _statusLabel.Text = "Wrong path clicked";

        _statusLabel = new Label
        {
            Name = "StatusLabel",
            Text = "Ready",
            AutoSize = true,
            Left = 36,
            Top = 250
        };

        Controls.AddRange([heading, input, smokeButton, toggle, modalButton, titleButton, wrong, _statusLabel]);
        Shown += (_, _) => Activate();
    }

    private void ShowProbeModal()
    {
        using var modal = new Form
        {
            Name = "ProbeModalWindow",
            Text = "HelpSys Probe Modal",
            Width = 520,
            Height = 280,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(45, 260),
            TopMost = true
        };

        var label = new Label
        {
            Name = "ModalLabel",
            Text = "Unexpected same-process modal",
            AutoSize = true,
            Left = 30,
            Top = 34
        };
        var close = new Button
        {
            Name = "CloseModalButton",
            Text = "Close modal",
            Left = 30,
            Top = 90,
            Width = 140,
            Height = 36,
            DialogResult = DialogResult.OK
        };
        modal.Controls.Add(label);
        modal.Controls.Add(close);
        modal.AcceptButton = close;
        modal.ShowDialog(this);
        _statusLabel.Text = "Modal closed";
    }
}

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
    private readonly CheckBox _probeToggle;
    private readonly System.Windows.Forms.Timer _commandTimer;
    private bool _modalOpen;

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

        _probeToggle = new CheckBox
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
        titleButton.Click += (_, _) => FlipTitle();

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

        Controls.AddRange([heading, input, smokeButton, _probeToggle, modalButton, titleButton, wrong, _statusLabel]);

        _commandTimer = new System.Windows.Forms.Timer { Interval = 120 };
        _commandTimer.Tick += (_, _) => ProcessExternalCommands();
        _commandTimer.Start();

        Shown += (_, _) => Activate();
        FormClosed += (_, _) => _commandTimer.Dispose();
    }

    private void ProcessExternalCommands()
    {
        if (ConsumeFlag("artifacts/virtual-target-title-flip.flag")) FlipTitle();
        if (ConsumeFlag("artifacts/virtual-target-toggle.flag"))
        {
            _probeToggle.Checked = !_probeToggle.Checked;
            _statusLabel.Text = $"Toggle={_probeToggle.Checked}";
        }
        if (ConsumeFlag("artifacts/virtual-target-open-modal.flag") && !_modalOpen)
            BeginInvoke(new Action(ShowProbeModal));
    }

    private static bool ConsumeFlag(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void FlipTitle()
    {
        Text = Text.EndsWith("-ALT", StringComparison.Ordinal)
            ? "PRIVATE-WINDOW-TITLE-319751"
            : "PRIVATE-WINDOW-TITLE-319751-ALT";
    }

    private void ShowProbeModal()
    {
        if (_modalOpen) return;
        _modalOpen = true;
        try
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
        finally
        {
            _modalOpen = false;
        }
    }
}

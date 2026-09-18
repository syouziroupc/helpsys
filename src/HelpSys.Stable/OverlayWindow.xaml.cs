using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace HelpSys.Stable;

public partial class OverlayWindow : Window
{
    private readonly double _dpiScale;
    private readonly ScreenObservation _observation;

    public OverlayWindow(ScreenObservation observation, PlanResult plan)
    {
        InitializeComponent();
        _observation = observation;

        var dpi = NativeMethods.GetDpiForWindow(observation.WindowHandle);
        _dpiScale = dpi > 0 ? 96d / dpi : 1d;

        // Size is expressed in WPF DIPs. Absolute desktop position is applied later
        // with SetWindowPos in physical pixels so mixed-DPI monitor origins remain correct.
        Width = PixelsToDips(observation.Width, dpi);
        Height = PixelsToDips(observation.Height, dpi);
        InstructionText.Text = BuildInstruction(plan);

        var target = ResolveTarget(observation, plan);
        if (target is not null)
        {
            TargetBorder.Visibility = Visibility.Visible;
            TargetBorder.Width = Math.Max(18, target.Value.Width * _dpiScale);
            TargetBorder.Height = Math.Max(18, target.Value.Height * _dpiScale);
            Canvas.SetLeft(TargetBorder, Math.Clamp(target.Value.X * _dpiScale, 0, Math.Max(0, Width - TargetBorder.Width)));
            Canvas.SetTop(TargetBorder, Math.Clamp(target.Value.Y * _dpiScale, 0, Math.Max(0, Height - TargetBorder.Height)));
        }

        Loaded += (_, _) => PositionInstruction(target);
        SourceInitialized += (_, _) =>
        {
            PositionPhysicalWindow();
            MakeClickThrough();
        };
    }

    internal static double PixelsToDips(double pixels, uint dpi)
        => pixels * 96d / (dpi > 0 ? dpi : 96d);

    private void PositionPhysicalWindow()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (!NativeMethods.SetWindowPos(
                hwnd,
                NativeMethods.HwndTopmost,
                _observation.X,
                _observation.Y,
                _observation.Width,
                _observation.Height,
                NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow))
            throw new InvalidOperationException("Overlay window could not be positioned.");
    }

    private void MakeClickThrough()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlExStyle);
        _ = NativeMethods.SetWindowLongPtr(
            hwnd,
            NativeMethods.GwlExStyle,
            style | NativeMethods.WsExTransparent | NativeMethods.WsExNoActivate | NativeMethods.WsExToolWindow);
    }

    private void PositionInstruction(RectD? target)
    {
        InstructionPanel.Measure(new Size(Math.Min(520, Math.Max(260, Width - 24)), double.PositiveInfinity));
        var desired = InstructionPanel.DesiredSize;
        var left = 14d;
        var top = Math.Max(14d, Height - desired.Height - 18d);

        if (target is not null && (target.Value.Y + target.Value.Height) * _dpiScale > top - 20)
            top = 14d;

        Canvas.SetLeft(InstructionPanel, left);
        Canvas.SetTop(InstructionPanel, top);
    }

    private static RectD? ResolveTarget(ScreenObservation observation, PlanResult plan)
    {
        if (plan.Status != "target") return null;

        if (!string.IsNullOrWhiteSpace(plan.TargetId))
        {
            var control = observation.Controls.FirstOrDefault(x => x.Id == plan.TargetId);
            if (control is not null)
            {
                return new RectD(
                    control.X / 1000d * observation.Width,
                    control.Y / 1000d * observation.Height,
                    control.Width / 1000d * observation.Width,
                    control.Height / 1000d * observation.Height);
            }
        }

        if (plan.Width > 0 && plan.Height > 0)
        {
            return new RectD(
                plan.X / 1000d * observation.Width,
                plan.Y / 1000d * observation.Height,
                plan.Width / 1000d * observation.Width,
                plan.Height / 1000d * observation.Height);
        }

        return null;
    }

    private static string BuildInstruction(PlanResult plan)
    {
        if (plan.Status == "clarify" && !string.IsNullOrWhiteSpace(plan.Question))
            return plan.Question!;
        if (plan.Status == "done")
            return "完了を確認しました。" + (string.IsNullOrWhiteSpace(plan.Instruction) ? string.Empty : " " + plan.Instruction);
        return plan.Instruction;
    }

    private readonly record struct RectD(double X, double Y, double Width, double Height);
}

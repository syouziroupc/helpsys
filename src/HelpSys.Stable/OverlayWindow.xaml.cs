using System.Windows;
using System.Windows.Interop;

namespace HelpSys.Stable;

public partial class OverlayWindow : Window
{
    public OverlayWindow(ScreenObservation observation, PlanResult plan)
    {
        InitializeComponent();

        Left = observation.X;
        Top = observation.Y;
        Width = observation.Width;
        Height = observation.Height;
        InstructionText.Text = BuildInstruction(plan);

        var target = ResolveTarget(observation, plan);
        if (target is not null)
        {
            TargetBorder.Visibility = Visibility.Visible;
            TargetBorder.Width = Math.Max(18, target.Value.Width);
            TargetBorder.Height = Math.Max(18, target.Value.Height);
            Canvas.SetLeft(TargetBorder, Math.Clamp(target.Value.X, 0, Math.Max(0, observation.Width - TargetBorder.Width)));
            Canvas.SetTop(TargetBorder, Math.Clamp(target.Value.Y, 0, Math.Max(0, observation.Height - TargetBorder.Height)));
        }

        Loaded += (_, _) => PositionInstruction(observation, target);
        SourceInitialized += (_, _) => MakeClickThrough();
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

    private void PositionInstruction(ScreenObservation observation, RectD? target)
    {
        InstructionPanel.Measure(new Size(Math.Min(520, observation.Width - 24), double.PositiveInfinity));
        var desired = InstructionPanel.DesiredSize;
        var left = 14d;
        var top = Math.Max(14d, observation.Height - desired.Height - 18d);

        if (target is not null && target.Value.Y + target.Value.Height > top - 20)
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

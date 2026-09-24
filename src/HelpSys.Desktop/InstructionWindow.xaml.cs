using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using HelpSys.Services;

namespace HelpSys;

public partial class InstructionWindow : Window
{
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private static readonly nint HwndTopmost = new(-1);

    public InstructionWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => OverlayWindow.MakeClickThrough(new WindowInteropHelper(this).Handle);
    }

    public void ShowNear(Rect physicalBounds, string instruction)
    {
        InstructionText.Text = instruction;
        if (!IsVisible) Show();

        var hwnd = new WindowInteropHelper(this).Handle;
        var work = MonitorPlacementService.GetWorkAreaForBounds(physicalBounds);

        static (int X, int Y, int Width, int Height) Geometry(Rect target, MonitorPlacementService.WorkArea workArea, double scale)
        {
            var width = Math.Max(1, (int)Math.Round(380 * scale));
            var height = Math.Max(1, (int)Math.Round(76 * scale));
            var gap = Math.Max(1, (int)Math.Round(14 * scale));
            var centerX = target.Left + target.Width / 2d;
            var x = (int)Math.Round(centerX - width / 2d);
            x = Math.Clamp(x, workArea.Left, Math.Max(workArea.Left, workArea.Right - width));
            var above = (int)Math.Floor(target.Top) - height - gap;
            var below = (int)Math.Ceiling(target.Bottom) + gap;
            var y = above >= workArea.Top ? above : below;
            y = Math.Clamp(y, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - height));
            return (x, y, width, height);
        }

        var initial = Geometry(physicalBounds, work, 1d);
        var initialPositioned = SetWindowPos(hwnd, HwndTopmost, initial.X, initial.Y, initial.Width, initial.Height, SwpNoActivate | SwpShowWindow);
        var dpiScale = MonitorPlacementService.GetDpiScaleForWindow(hwnd);
        var final = Geometry(physicalBounds, work, dpiScale);
        var positioned = SetWindowPos(hwnd, HwndTopmost, final.X, final.Y, final.Width, final.Height, SwpNoActivate | SwpShowWindow);

        if (MonitorPlacementService.TryGetWindowBounds(hwnd, out var actual))
        {
            LocalLogService.Write(
                "instruction_placement",
                $"initialOk={initialPositioned};ok={positioned};dpiScale={dpiScale:F3};target={physicalBounds};requested={new Rect(final.X, final.Y, final.Width, final.Height)};actual={actual}");
        }
        else
        {
            LocalLogService.Write(
                "instruction_placement",
                $"initialOk={initialPositioned};ok={positioned};dpiScale={dpiScale:F3};target={physicalBounds};requested={new Rect(final.X, final.Y, final.Width, final.Height)};actual=unavailable;win32={Marshal.GetLastWin32Error()}");
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
}

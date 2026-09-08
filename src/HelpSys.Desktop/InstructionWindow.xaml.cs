using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

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

        const int width = 380;
        const int height = 76;
        var centerX = physicalBounds.Left + physicalBounds.Width / 2;
        var x = (int)Math.Round(centerX - width / 2d);
        var preferredY = (int)Math.Floor(physicalBounds.Top) - height - 14;
        var y = preferredY >= 0
            ? preferredY
            : (int)Math.Ceiling(physicalBounds.Bottom) + 14;

        SetWindowPos(new WindowInteropHelper(this).Handle, HwndTopmost, x, y, width, height, SwpNoActivate | SwpShowWindow);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
}

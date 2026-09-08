using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace HelpSys;

public partial class OverlayWindow : Window
{
    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x00000020L;
    private const long WsExNoActivate = 0x08000000L;
    private const long WsExToolWindow = 0x00000080L;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private static readonly nint HwndTopmost = new(-1);

    private readonly InstructionWindow _instruction = new();

    public OverlayWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => MakeClickThrough(new WindowInteropHelper(this).Handle);
    }

    public void ShowTarget(Rect physicalBounds, string instruction)
    {
        if (!IsVisible) Show();

        var hwnd = new WindowInteropHelper(this).Handle;
        const int padding = 7;
        var x = (int)Math.Floor(physicalBounds.Left) - padding;
        var y = (int)Math.Floor(physicalBounds.Top) - padding;
        var width = Math.Max(18, (int)Math.Ceiling(physicalBounds.Width) + padding * 2);
        var height = Math.Max(18, (int)Math.Ceiling(physicalBounds.Height) + padding * 2);

        SetWindowPos(hwnd, HwndTopmost, x, y, width, height, SwpNoActivate | SwpShowWindow);
        _instruction.ShowNear(physicalBounds, instruction);
    }

    public new void Hide()
    {
        _instruction.Hide();
        base.Hide();
    }

    public new void Close()
    {
        _instruction.Close();
        base.Close();
    }

    internal static void MakeClickThrough(nint hwnd)
    {
        var style = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        style |= WsExTransparent | WsExNoActivate | WsExToolWindow;
        SetWindowLongPtr(hwnd, GwlExStyle, new nint(style));
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr64(nint hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(nint hWnd, int nIndex);

    private static nint GetWindowLongPtr(nint hWnd, int nIndex) =>
        nint.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : new nint(GetWindowLong32(hWnd, nIndex));

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr64(nint hWnd, int nIndex, nint dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong32(nint hWnd, int nIndex, int dwNewLong);

    private static nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong) =>
        nint.Size == 8 ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong) : new nint(SetWindowLong32(hWnd, nIndex, dwNewLong.ToInt32()));

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
}

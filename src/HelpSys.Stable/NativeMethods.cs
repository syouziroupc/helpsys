using System.Runtime.InteropServices;
using System.Text;

namespace HelpSys.Stable;

internal static class NativeMethods
{
    public const int HotKeyId = 0x4853;
    public const uint ModNoRepeat = 0x4000;
    public const uint VkF8 = 0x77;
    public const int WmHotKey = 0x0312;
    public const int GwlExStyle = -20;
    public const nint WsExTransparent = 0x00000020;
    public const nint WsExToolWindow = 0x00000080;
    public const nint WsExNoActivate = 0x08000000;

    [DllImport("user32.dll")]
    public static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(nint hWnd, out Rect rect);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint hWnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindow(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterHotKey(nint hWnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterHotKey(nint hWnd, int id);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static extern nint GetWindowLongPtr(nint hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    public static extern nint SetWindowLongPtr(nint hWnd, int index, nint newLong);

    public static string GetTitle(nint hWnd)
    {
        var sb = new StringBuilder(512);
        _ = GetWindowText(hWnd, sb, sb.Capacity);
        return sb.ToString().Trim();
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
        public int Width => Math.Max(0, Right - Left);
        public int Height => Math.Max(0, Bottom - Top);
    }
}

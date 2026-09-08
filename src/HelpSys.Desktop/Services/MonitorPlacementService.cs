using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace HelpSys.Services;

public static class MonitorPlacementService
{
    private const uint MonitorDefaultToNearest = 0x00000002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    public readonly record struct WorkArea(int Left, int Top, int Right, int Bottom)
    {
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    public static void MoveWindowToCursorMonitorBottomRight(Window window, int margin = 16)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == nint.Zero || !GetWindowRect(hwnd, out var rect)) return;

        var work = GetWorkAreaForCursor();
        var width = Math.Max(1, rect.Right - rect.Left);
        var height = Math.Max(1, rect.Bottom - rect.Top);
        var x = work.Right - width - margin;
        var y = work.Bottom - height - margin;
        SetWindowPos(hwnd, nint.Zero, x, y, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
    }

    public static WorkArea GetWorkAreaForCursor()
    {
        if (!GetCursorPos(out var point)) point = new PointNative { X = 0, Y = 0 };
        return GetWorkAreaForPoint(point.X, point.Y);
    }

    public static WorkArea GetWorkAreaForBounds(Rect bounds)
    {
        var x = bounds.IsEmpty ? 0 : (int)Math.Round(bounds.Left + bounds.Width / 2d);
        var y = bounds.IsEmpty ? 0 : (int)Math.Round(bounds.Top + bounds.Height / 2d);
        return GetWorkAreaForPoint(x, y);
    }

    private static WorkArea GetWorkAreaForPoint(int x, int y)
    {
        var monitor = MonitorFromPoint(new PointNative { X = x, Y = y }, MonitorDefaultToNearest);
        if (monitor == nint.Zero) return new WorkArea(0, 0, GetSystemMetrics(0), GetSystemMetrics(1));

        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return new WorkArea(0, 0, GetSystemMetrics(0), GetSystemMetrics(1));
        return new WorkArea(info.Work.Left, info.Work.Top, info.Work.Right, info.Work.Bottom);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PointNative
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RectNative
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public RectNative Monitor;
        public RectNative Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out PointNative point);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(PointNative point, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hwnd, out RectNative rect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}

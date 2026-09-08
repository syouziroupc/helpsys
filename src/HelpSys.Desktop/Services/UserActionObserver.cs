using System.Runtime.InteropServices;
using System.Windows;

namespace HelpSys.Services;

public sealed class UserActionObserver : IDisposable
{
    private const int WhMouseLl = 14;
    private const int WhKeyboardLl = 13;
    private const int WmLButtonUp = 0x0202;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyUp = 0x0105;

    private readonly HookProc _mouseProc;
    private readonly HookProc _keyboardProc;
    private IntPtr _mouseHook;
    private IntPtr _keyboardHook;

    public event Action<Point>? LeftClick;
    public event Action<int>? KeyReleased;

    public UserActionObserver()
    {
        _mouseProc = MouseHookCallback;
        _keyboardProc = KeyboardHookCallback;
    }

    public bool IsRunning => _mouseHook != IntPtr.Zero || _keyboardHook != IntPtr.Zero;

    public void Start()
    {
        if (IsRunning) return;
        _mouseHook = SetWindowsHookEx(WhMouseLl, _mouseProc, IntPtr.Zero, 0);
        _keyboardHook = SetWindowsHookEx(WhKeyboardLl, _keyboardProc, IntPtr.Zero, 0);

        if (_mouseHook == IntPtr.Zero || _keyboardHook == IntPtr.Zero)
        {
            Stop();
            throw new InvalidOperationException("ユーザー操作の観測を開始できませんでした。");
        }
    }

    public void Stop()
    {
        if (_mouseHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }
        if (_keyboardHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
        }
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam.ToInt32() == WmLButtonUp)
        {
            var data = Marshal.PtrToStructure<MsllHookStruct>(lParam);
            var point = new Point(data.Point.X, data.Point.Y);
            Application.Current?.Dispatcher.BeginInvoke(() => LeftClick?.Invoke(point));
        }
        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        var message = wParam.ToInt32();
        if (nCode >= 0 && (message == WmKeyUp || message == WmSysKeyUp))
        {
            var data = Marshal.PtrToStructure<KbdllHookStruct>(lParam);
            var key = unchecked((int)data.VirtualKeyCode);
            Application.Current?.Dispatcher.BeginInvoke(() => KeyReleased?.Invoke(key));
        }
        return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    public void Dispose() => Stop();

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct PointStruct { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MsllHookStruct
    {
        public PointStruct Point;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdllHookStruct
    {
        public uint VirtualKeyCode;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc callback, IntPtr moduleHandle, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
}

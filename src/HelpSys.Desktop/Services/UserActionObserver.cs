using System.Runtime.InteropServices;
using System.Windows;

namespace HelpSys.Services;

public sealed record KeyObservation(int VirtualKey, bool Control, bool Shift, bool Alt, bool Windows);

public sealed class UserActionObserver : IDisposable
{
    private const int WhMouseLl = 14;
    private const int WhKeyboardLl = 13;
    private const int WmLButtonUp = 0x0202;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const int VkControl = 0x11;
    private const int VkShift = 0x10;
    private const int VkMenu = 0x12;
    private const int VkLWin = 0x5B;
    private const int VkRWin = 0x5C;

    private readonly HookProc _mouseProc;
    private readonly HookProc _keyboardProc;
    private readonly object _keyStateGate = new();
    private readonly Dictionary<int, KeyObservation> _keyDownSnapshots = new();
    private IntPtr _mouseHook;
    private IntPtr _keyboardHook;

    public event Action<Point>? LeftClick;
    public event Action<KeyObservation>? KeyReleased;

    public UserActionObserver()
    {
        _mouseProc = MouseHookCallback;
        _keyboardProc = KeyboardHookCallback;
    }

    public bool IsRunning => _mouseHook != IntPtr.Zero || _keyboardHook != IntPtr.Zero;

    public void Start()
    {
        if (IsRunning) return;
        lock (_keyStateGate) _keyDownSnapshots.Clear();
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
        lock (_keyStateGate) _keyDownSnapshots.Clear();
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
        if (nCode < 0) return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);

        var message = wParam.ToInt32();
        if (message is not (WmKeyDown or WmSysKeyDown or WmKeyUp or WmSysKeyUp))
            return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);

        var data = Marshal.PtrToStructure<KbdllHookStruct>(lParam);
        var key = unchecked((int)data.VirtualKeyCode);

        if (message is WmKeyDown or WmSysKeyDown)
        {
            if (!IsModifierKey(key))
            {
                var snapshot = CaptureObservation(key);
                lock (_keyStateGate) _keyDownSnapshots[key] = snapshot;
            }
            return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
        }

        KeyObservation observation;
        if (!IsModifierKey(key))
        {
            lock (_keyStateGate)
            {
                if (_keyDownSnapshots.Remove(key, out var stored)) observation = stored;
                else observation = CaptureObservation(key);
            }
        }
        else
        {
            observation = CaptureObservation(key);
        }

        Application.Current?.Dispatcher.BeginInvoke(() => KeyReleased?.Invoke(observation));
        return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    private static KeyObservation CaptureObservation(int key) => new(
        key,
        IsDown(VkControl) || key == VkControl,
        IsDown(VkShift) || key == VkShift,
        IsDown(VkMenu) || key == VkMenu,
        IsDown(VkLWin) || IsDown(VkRWin) || key == VkLWin || key == VkRWin);

    private static bool IsModifierKey(int key) => key is VkControl or VkShift or VkMenu or VkLWin or VkRWin;

    private static bool IsDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

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

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);
}

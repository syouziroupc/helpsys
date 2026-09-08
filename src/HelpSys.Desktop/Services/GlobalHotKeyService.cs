using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace HelpSys.Services;

public sealed class GlobalHotKeyService : IDisposable
{
    private const int WmHotKey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint VkH = 0x48;
    private const int HotKeyId = 0x4853;

    private HwndSource? _source;
    private nint _handle;

    public event EventHandler? Activated;

    public bool Register(nint windowHandle)
    {
        _handle = windowHandle;
        _source = HwndSource.FromHwnd(windowHandle);
        _source?.AddHook(WndProc);
        return RegisterHotKey(windowHandle, HotKeyId, ModControl | ModAlt, VkH);
    }

    public void Dispose()
    {
        if (_handle != 0) UnregisterHotKey(_handle, HotKeyId);
        _source?.RemoveHook(WndProc);
        _source = null;
        _handle = 0;
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WmHotKey && wParam.ToInt32() == HotKeyId)
        {
            handled = true;
            Activated?.Invoke(this, EventArgs.Empty);
        }
        return 0;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint hWnd, int id);
}

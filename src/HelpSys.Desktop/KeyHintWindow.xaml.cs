using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using HelpSys.Services;

namespace HelpSys;

public partial class KeyHintWindow : Window
{
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private static readonly nint HwndTopmost = new(-1);

    public KeyHintWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => OverlayWindow.MakeClickThrough(new WindowInteropHelper(this).Handle);
    }

    public void ShowKeys(string? keySpec, string instruction)
    {
        KeysText.Text = FormatKeys(keySpec);
        InstructionText.Text = instruction;
        if (!IsVisible) Show();

        var work = MonitorPlacementService.GetWorkAreaForCursor();
        const int width = 520;
        const int height = 170;
        var x = work.Left + Math.Max(0, (work.Width - width) / 2);
        var y = work.Top + Math.Max(16, (int)(work.Height * 0.12));
        SetWindowPos(new WindowInteropHelper(this).Handle, HwndTopmost, x, y, width, height, SwpNoActivate | SwpShowWindow);
    }

    private static string FormatKeys(string? keySpec)
    {
        if (string.IsNullOrWhiteSpace(keySpec)) return "[ キー ]";
        return string.Join("   +   ", keySpec.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => $"[ {FriendlyKey(x)} ]"));
    }

    private static string FriendlyKey(string key) => key.Trim().ToLowerInvariant() switch
    {
        "ctrl" or "control" => "Ctrl",
        "alt" => "Alt",
        "shift" => "Shift",
        "windows" or "win" => "⊞ Windows",
        "enter" or "return" => "Enter",
        "left" => "←",
        "right" => "→",
        "up" => "↑",
        "down" => "↓",
        "escape" or "esc" => "Esc",
        _ => key.Trim().ToUpperInvariant()
    };

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
}

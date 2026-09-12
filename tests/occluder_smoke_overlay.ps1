$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName PresentationFramework
Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName WindowsBase
Add-Type -AssemblyName WindowsFormsIntegration
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class HelpSysNoActivateNative
{
    public const int GWL_EXSTYLE = -20;
    public const long WS_EX_TOOLWINDOW = 0x00000080L;
    public const long WS_EX_NOACTIVATE = 0x08000000L;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)]
    public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
    public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
}
'@

$window = New-Object System.Windows.Window
$window.Title = 'Unrelated Overlay Smoke'
$window.Width = 340
$window.Height = 130
$window.WindowStartupLocation = [System.Windows.WindowStartupLocation]::CenterScreen
$window.Topmost = $true
$window.ShowInTaskbar = $false
$window.ResizeMode = [System.Windows.ResizeMode]::NoResize
$window.WindowStyle = [System.Windows.WindowStyle]::ToolWindow
$window.ShowActivated = $false
$window.Focusable = $false
$window.Background = [System.Windows.Media.Brushes]::White

$border = New-Object System.Windows.Controls.Border
$border.Background = [System.Windows.Media.Brushes]::White
$border.Padding = [System.Windows.Thickness]::new(8)

$label = New-Object System.Windows.Controls.TextBlock
$label.Text = 'UNRELATED OVERLAY - MUST BE REDACTED'
$label.FontFamily = New-Object System.Windows.Media.FontFamily('Segoe UI')
$label.FontSize = 18
$label.FontWeight = [System.Windows.FontWeights]::Bold
$label.Foreground = [System.Windows.Media.Brushes]::Black
$label.TextAlignment = [System.Windows.TextAlignment]::Center
$label.VerticalAlignment = [System.Windows.VerticalAlignment]::Center
$label.HorizontalAlignment = [System.Windows.HorizontalAlignment]::Center
$label.TextWrapping = [System.Windows.TextWrapping]::Wrap
$border.Child = $label
$window.Content = $border

$window.Add_SourceInitialized({
    $helper = New-Object System.Windows.Interop.WindowInteropHelper($window)
    $hwnd = $helper.Handle
    if ($hwnd -eq [IntPtr]::Zero) { throw 'Occluder helper did not obtain a native HWND.' }

    $current = [HelpSysNoActivateNative]::GetWindowLongPtr($hwnd, [HelpSysNoActivateNative]::GWL_EXSTYLE).ToInt64()
    $desired = $current -bor [HelpSysNoActivateNative]::WS_EX_NOACTIVATE -bor [HelpSysNoActivateNative]::WS_EX_TOOLWINDOW
    [void][HelpSysNoActivateNative]::SetWindowLongPtr($hwnd, [HelpSysNoActivateNative]::GWL_EXSTYLE, [IntPtr]$desired)
})

$window.Add_ContentRendered({
    New-Item -ItemType Directory -Force -Path artifacts | Out-Null
    $helper = New-Object System.Windows.Interop.WindowInteropHelper($window)
    $hwnd = $helper.Handle
    $exStyle = [HelpSysNoActivateNative]::GetWindowLongPtr($hwnd, [HelpSysNoActivateNative]::GWL_EXSTYLE).ToInt64()
    [pscustomobject]@{
        hwnd = $hwnd.ToInt64()
        left = [math]::Round($window.Left)
        top = [math]::Round($window.Top)
        width = [math]::Round($window.ActualWidth)
        height = [math]::Round($window.ActualHeight)
        processId = $PID
        showActivated = $window.ShowActivated
        topmost = $window.Topmost
        noActivateStyle = (($exStyle -band [HelpSysNoActivateNative]::WS_EX_NOACTIVATE) -ne 0)
    } | ConvertTo-Json -Compress | Set-Content -Path 'artifacts/occluder-overlay-bounds.json' -Encoding UTF8
})

$window.Add_Closed({
    [System.Windows.Threading.Dispatcher]::CurrentDispatcher.BeginInvokeShutdown(
        [System.Windows.Threading.DispatcherPriority]::Background)
})

$window.Show()
[System.Windows.Threading.Dispatcher]::Run()

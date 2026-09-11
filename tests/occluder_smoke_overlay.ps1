$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName PresentationFramework
Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName WindowsBase

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

$window.Add_ContentRendered({
    New-Item -ItemType Directory -Force -Path artifacts | Out-Null
    [pscustomobject]@{
        left = [math]::Round($window.Left)
        top = [math]::Round($window.Top)
        width = [math]::Round($window.ActualWidth)
        height = [math]::Round($window.ActualHeight)
        processId = $PID
        showActivated = $window.ShowActivated
        topmost = $window.Topmost
    } | ConvertTo-Json -Compress | Set-Content -Path 'artifacts/occluder-overlay-bounds.json' -Encoding UTF8
})

$window.Add_Closed({
    [System.Windows.Threading.Dispatcher]::CurrentDispatcher.BeginInvokeShutdown(
        [System.Windows.Threading.DispatcherPriority]::Background)
})

$window.Show()
[System.Windows.Threading.Dispatcher]::Run()

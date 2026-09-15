using System.Windows;
using System.Windows.Interop;

namespace HelpSys;

public partial class MainWindow
{
    private const int WmMouseActivate = 0x0021;
    private static readonly bool MouseActivationHandoffHandlersRegistered = RegisterMouseActivationHandoffHandlers();
    private HwndSource? _mouseActivationHandoffSource;
    private HwndSourceHook? _mouseActivationHandoffHook;

    private static bool RegisterMouseActivationHandoffHandlers()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(MouseActivationHandoff_Loaded),
            true);
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            FrameworkElement.UnloadedEvent,
            new RoutedEventHandler(MouseActivationHandoff_Unloaded),
            true);
        return true;
    }

    private static void MouseActivationHandoff_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not MainWindow window || window._mouseActivationHandoffSource is not null) return;

        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == nint.Zero) return;
        var source = HwndSource.FromHwnd(hwnd);
        if (source is null) return;

        window._mouseActivationHandoffHook = window.MouseActivationHandoff_WndProc;
        window._mouseActivationHandoffSource = source;
        source.AddHook(window._mouseActivationHandoffHook);
    }

    private static void MouseActivationHandoff_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not MainWindow window) return;
        var source = window._mouseActivationHandoffSource;
        var hook = window._mouseActivationHandoffHook;
        window._mouseActivationHandoffSource = null;
        window._mouseActivationHandoffHook = null;
        if (source is null || hook is null) return;
        try { source.RemoveHook(hook); } catch { }
    }

    private nint MouseActivationHandoff_WndProc(
        nint hwnd,
        int message,
        nint wParam,
        nint lParam,
        ref bool handled)
    {
        if (message != WmMouseActivate) return nint.Zero;

        // WM_MOUSEACTIVATE arrives before Windows activates HelpSys. At this point
        // GetForegroundWindow still identifies the real work surface the user is leaving.
        // Record it twice to satisfy the interaction-candidate confirmation without weakening
        // passive foreground tracking or the Privacy Gate.
        _systemContext.CaptureExternalForegroundForAssistantInteraction();
        _systemContext.CaptureExternalForegroundForAssistantInteraction();
        return nint.Zero;
    }
}

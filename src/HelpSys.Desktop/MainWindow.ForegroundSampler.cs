using System.Windows;

namespace HelpSys;

public partial class MainWindow
{
    private static readonly bool ForegroundSamplerClassHandlersRegistered = RegisterForegroundSamplerClassHandlers();
    private System.Threading.Timer? _backgroundForegroundSampler;

    private static bool RegisterForegroundSamplerClassHandlers()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(BackgroundForegroundSampler_Loaded),
            true);
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            FrameworkElement.UnloadedEvent,
            new RoutedEventHandler(BackgroundForegroundSampler_Unloaded),
            true);
        return true;
    }

    private static void BackgroundForegroundSampler_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not MainWindow window || window._backgroundForegroundSampler is not null) return;

        // Foreground ownership must keep being sampled even while the WPF dispatcher is busy with
        // UI Automation, rendering, speech UI, or planning. A ThreadPool timer prevents those UI
        // workloads from creating a blind spot immediately before the user clicks HelpSys.
        window._backgroundForegroundSampler = new System.Threading.Timer(
            static state =>
            {
                if (state is not MainWindow current) return;
                try { current._systemContext.CaptureExternalForegroundForAssistantInteraction(); }
                catch { }
            },
            window,
            TimeSpan.Zero,
            TimeSpan.FromMilliseconds(25));
    }

    private static void BackgroundForegroundSampler_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not MainWindow window) return;
        var timer = Interlocked.Exchange(ref window._backgroundForegroundSampler, null);
        try { timer?.Dispose(); } catch { }
    }
}

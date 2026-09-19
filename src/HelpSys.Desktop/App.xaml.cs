using System.Threading;
using System.Windows;
using HelpSys.Services;

namespace HelpSys;

public partial class App : Application
{
    private Mutex? _singleInstanceMutex;
    private ListeningOverlayWindow? _smokeListeningOverlay;

    protected override void OnStartup(StartupEventArgs e)
    {
        if (UiAutomationObserverHost.IsObserverProcess)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            base.OnStartup(e);
            try { UiAutomationObserverHost.RunAsync().GetAwaiter().GetResult(); }
            finally { Shutdown(); }
            return;
        }

        _singleInstanceMutex = new Mutex(initiallyOwned: true, name: @"Local\HelpSys.Desktop.SingleInstance", createdNew: out var createdNew);
        if (!createdNew)
        {
            Shutdown();
            return;
        }

        base.OnStartup(e);
        MainWindow = new MainWindow();
        MainWindow.Show();

        // Windows CI uses this opt-in hook only to render and screenshot the real WPF overlay.
        // It is inert in normal builds and does not touch the microphone.
        if (Environment.GetEnvironmentVariable("HELPSYS_SMOKE_LISTENING_OVERLAY") == "1")
        {
            _smokeListeningOverlay = new ListeningOverlayWindow();
            _smokeListeningOverlay.ShowListening(0.62);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _smokeListeningOverlay?.Close(); } catch { }
        _smokeListeningOverlay = null;
        try { _singleInstanceMutex?.ReleaseMutex(); } catch (ApplicationException) { }
        _singleInstanceMutex?.Dispose();
        try { HelpSys.Services.UiAutomationScanner.ShutdownSharedObserver(); } catch { }
        base.OnExit(e);
    }
}

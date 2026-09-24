using System.Threading;
using System.Windows;
using System.Windows.Threading;
using HelpSys.Services;

namespace HelpSys;

public partial class App : Application
{
    private Mutex? _singleInstanceMutex;
    private ListeningOverlayWindow? _smokeListeningOverlay;

    protected override void OnStartup(StartupEventArgs e)
    {
        ProcessDiagnostics.Initialize();
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        SessionEnding += OnSessionEnding;

        _singleInstanceMutex = new Mutex(initiallyOwned: true, name: @"Local\HelpSys.Desktop.SingleInstance", createdNew: out var createdNew);
        if (!createdNew)
        {
            ProcessDiagnostics.MarkExitReason("single_instance_rejected", overwrite: true);
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
        ProcessDiagnostics.MarkExitReasonIfUnset("application_exit");
        ProcessDiagnostics.Log("application_exit", new
        {
            e.ApplicationExitCode,
            exitReason = ProcessDiagnostics.ExitReason
        });

        try { _smokeListeningOverlay?.Close(); } catch { }
        _smokeListeningOverlay = null;
        try { _singleInstanceMutex?.ReleaseMutex(); } catch (ApplicationException) { }
        _singleInstanceMutex?.Dispose();
        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        SessionEnding -= OnSessionEnding;
        base.OnExit(e);
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ProcessDiagnostics.MarkExitReason("dispatcher_unhandled_exception", e.Exception.GetType().FullName, overwrite: true);
        ProcessDiagnostics.LogException("dispatcher_unhandled_exception", e.Exception);
    }

    private static void OnSessionEnding(object sender, SessionEndingCancelEventArgs e)
    {
        ProcessDiagnostics.MarkExitReason("windows_session_ending", e.ReasonSessionEnding.ToString(), overwrite: true);
        ProcessDiagnostics.Log("windows_session_ending", new { reason = e.ReasonSessionEnding.ToString() });
    }
}

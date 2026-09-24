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
        if (UiAutomationObserverHost.IsObserverProcess)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            base.OnStartup(e);
            try
            {
                // Run the pipe loop off the WPF dispatcher. Awaiting redirected stdin from the
                // Startup thread and synchronously blocking that same dispatcher would deadlock
                // the continuation before the first observer response.
                Task.Run(UiAutomationObserverHost.RunAsync).GetAwaiter().GetResult();
            }
            finally { Shutdown(); }
            return;
        }

        LocalLogService.Initialize();
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        SessionEnding += OnSessionEnding;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        _singleInstanceMutex = new Mutex(initiallyOwned: true, name: @"Local\HelpSys.Desktop.SingleInstance", createdNew: out var createdNew);
        if (!createdNew)
        {
            LocalLogService.MarkExitReason("single_instance_rejected", overwrite: true);
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
        LocalLogService.MarkExitReasonIfUnset("application_exit");
        LocalLogService.Write("application_exit", $"code={e.ApplicationExitCode};reason={LocalLogService.ExitReason}");

        try { _smokeListeningOverlay?.Close(); } catch { }
        _smokeListeningOverlay = null;
        try { _singleInstanceMutex?.ReleaseMutex(); } catch (ApplicationException) { }
        _singleInstanceMutex?.Dispose();
        try { HelpSys.Services.UiAutomationScanner.ShutdownSharedObserver(); } catch { }
        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        SessionEnding -= OnSessionEnding;
        AppDomain.CurrentDomain.UnhandledException -= OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
        LocalLogService.Shutdown();
        base.OnExit(e);
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LocalLogService.MarkExitReason("dispatcher_unhandled_exception", e.Exception.GetType().FullName, overwrite: true);
        LocalLogService.WriteException("dispatcher_unhandled_exception", e.Exception);
    }

    private static void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        LocalLogService.MarkExitReason("appdomain_unhandled_exception", overwrite: true);
        if (e.ExceptionObject is Exception exception)
            LocalLogService.WriteException("appdomain_unhandled_exception", exception);
        else
            LocalLogService.Write("appdomain_unhandled_exception", $"terminating={e.IsTerminating};value={e.ExceptionObject}");
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        => LocalLogService.WriteException("unobserved_task_exception", e.Exception);

    private static void OnSessionEnding(object sender, SessionEndingCancelEventArgs e)
    {
        LocalLogService.MarkExitReason("windows_session_ending", e.ReasonSessionEnding.ToString(), overwrite: true);
        LocalLogService.Write("windows_session_ending", e.ReasonSessionEnding.ToString());
    }
}

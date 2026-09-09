using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;

namespace HelpSys;

public partial class App : Application
{
    private const int SwRestore = 9;
    private Mutex? _singleInstanceMutex;
    private ListeningOverlayWindow? _smokeListeningOverlay;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstanceMutex = new Mutex(initiallyOwned: true, name: @"Local\HelpSys.Desktop.SingleInstance", createdNew: out var createdNew);
        if (!createdNew)
        {
            TryActivateExistingInstance();
            Shutdown();
            return;
        }

        base.OnStartup(e);
        MainWindow = new MainWindow();
        MainWindow.Show();

        if (Environment.GetEnvironmentVariable("HELPSYS_SMOKE_LISTENING_OVERLAY") == "1")
        {
            _smokeListeningOverlay = new ListeningOverlayWindow();
            _smokeListeningOverlay.ShowListening(0.62);
        }
    }

    private static void TryActivateExistingInstance()
    {
        try
        {
            var currentId = Environment.ProcessId;
            var processName = Process.GetCurrentProcess().ProcessName;
            foreach (var process in Process.GetProcessesByName(processName))
            {
                using (process)
                {
                    if (process.Id == currentId) continue;
                    var hwnd = process.MainWindowHandle;
                    if (hwnd == IntPtr.Zero) continue;
                    ShowWindow(hwnd, SwRestore);
                    SetForegroundWindow(hwnd);
                    break;
                }
            }
        }
        catch
        {
            // The existing process still owns the mutex. If Windows refuses foreground activation,
            // exit the duplicate instance without risking two guidance observers/microphone owners.
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _smokeListeningOverlay?.Close(); } catch { }
        _smokeListeningOverlay = null;
        try { _singleInstanceMutex?.ReleaseMutex(); } catch (ApplicationException) { }
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}

using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Windows;

namespace HelpSys.Education;

public partial class App : Application
{
    private const int SwRestore = 9;
    private Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        Curriculum.Validate();
        CurriculumProgression.Validate();

        if (e.Args.Any(x => string.Equals(x, "--self-test", StringComparison.OrdinalIgnoreCase)))
        {
            RunProgressRecoverySelfTest();
            Shutdown(0);
            return;
        }

        _singleInstanceMutex = new Mutex(initiallyOwned: true, name: @"Local\HelpSys.Education.SingleInstance", createdNew: out var createdNew);
        if (!createdNew)
        {
            TryActivateExistingInstance();
            Shutdown();
            return;
        }

        base.OnStartup(e);
        MainWindow = new MainWindow();
        MainWindow.Show();
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
        catch { }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _singleInstanceMutex?.ReleaseMutex(); } catch (ApplicationException) { }
        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;
        base.OnExit(e);
    }

    private static void RunProgressRecoverySelfTest()
    {
        var root = Path.Combine(Path.GetTempPath(), $"HelpSys.Education.SelfTest.{Environment.ProcessId}.{Guid.NewGuid():N}");
        try
        {
            var store = new ProgressStore(root);
            store.MarkEducationCompleted("self-test");
            store.MarkPracticeCompleted("self-test");
            store.RecordQuizResult("self-test", 100);
            store.Save();

            if (!File.Exists(store.BackupPathForSelfTest))
                throw new InvalidOperationException("進捗バックアップが作成されませんでした。");

            File.WriteAllText(store.PrimaryPathForSelfTest, "{ deliberately broken json");
            var recovered = new ProgressStore(root);
            var progress = recovered.Get("self-test");
            if (!progress.EducationCompleted || !progress.PracticeCompleted || !progress.TestPassed || progress.BestQuizScore != 100)
                throw new InvalidOperationException("破損した進捗ファイルをバックアップから復旧できませんでした。");

            using var _ = JsonDocument.Parse(File.ReadAllText(recovered.PrimaryPathForSelfTest));
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
            catch { }
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}

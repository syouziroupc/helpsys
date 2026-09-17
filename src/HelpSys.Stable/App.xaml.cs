using System.Text;
using System.Windows;

namespace HelpSys.Stable;

public partial class App : Application
{
    public App()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) LogException("appdomain_unhandled", ex);
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            LogException("task_unobserved", e.Exception);
            e.SetObserved();
        };
    }

    protected override void OnDispatcherUnhandledException(System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        LogException("dispatcher_unhandled", e.Exception);
        MessageBox.Show(
            "HelpSys内部で予期しないエラーを処理しました。PC操作は実行されていません。\n\n" +
            "案内を再実行できます。問題が続く場合はログを確認してください。",
            VersionInfo.ProductName,
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }

    public static void LogException(string area, Exception exception)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "HelpSysStable");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "errors.log");
            var builder = new StringBuilder()
                .AppendLine($"[{DateTimeOffset.Now:O}] {VersionInfo.ProductName} {area}")
                .AppendLine(exception.ToString())
                .AppendLine();
            File.AppendAllText(path, builder.ToString());
        }
        catch
        {
        }
    }
}

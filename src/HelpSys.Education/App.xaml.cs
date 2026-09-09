using System.Windows;

namespace HelpSys.Education;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        Curriculum.Validate();

        if (e.Args.Any(x => string.Equals(x, "--self-test", StringComparison.OrdinalIgnoreCase)))
        {
            Shutdown(0);
            return;
        }

        base.OnStartup(e);
        MainWindow = new MainWindow();
        MainWindow.Show();
    }
}

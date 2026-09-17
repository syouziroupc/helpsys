using System.Windows;

namespace HelpSys.Stable;

public partial class App : Application
{
    protected override void OnDispatcherUnhandledException(System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            "HelpSysで予期しないエラーが発生しました。操作は実行されていません。\n\n" + e.Exception.Message,
            "HelpSys Stable",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }
}

using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using HelpSys.Services;

namespace HelpSys;

public partial class MainWindow : Window
{
    private readonly UiAutomationScanner _scanner = new();
    private readonly GuidePlanner _planner = new();
    private readonly GlobalHotKeyService _hotKey = new();
    private readonly OverlayWindow _overlay = new();
    private CancellationTokenSource? _scanCts;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        SourceInitialized += OnSourceInitialized;
        Closing += OnClosing;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        PositionNearBottomRight();
        RequestBox.Focus();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        _hotKey.Activated += (_, _) => ActivateHelp();
        if (!_hotKey.Register(hwnd))
        {
            StateText.Text = "待機中。Ctrl+Alt+H は他のアプリが使用中です。";
        }
    }

    private void PositionNearBottomRight()
    {
        Left = SystemParameters.WorkArea.Right - Width - 20;
        Top = SystemParameters.WorkArea.Bottom - Height - 20;
    }

    private void ActivateHelp()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        RequestBox.Focus();
        StateText.Text = "何をしたいですか？";
    }

    private async void GuideButton_Click(object sender, RoutedEventArgs e) => await StartGuideAsync();

    private async void RequestBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await StartGuideAsync();
    }

    private async Task StartGuideAsync()
    {
        var request = RequestBox.Text.Trim();
        if (request.Length == 0)
        {
            StateText.Text = "やりたいことを入力してください。";
            return;
        }

        _scanCts?.Cancel();
        _scanCts?.Dispose();
        _scanCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        GuideButton.IsEnabled = false;
        StateText.Text = "画面上の操作対象を確認しています…";

        try
        {
            var plan = _planner.CreateFirstStep(request);
            var target = await _scanner.FindBestTargetAsync(plan.TargetHints, _scanCts.Token);
            if (target is null)
            {
                _overlay.Hide();
                StateText.Text = "対象を特定できませんでした。画面画像による確認は次の実装段階です。";
                return;
            }

            _overlay.ShowTarget(target.Bounds, plan.Instruction);
            StateText.Text = $"案内中: {DisplayName(target.Name, target.ControlType)}";
        }
        catch (OperationCanceledException)
        {
            StateText.Text = "画面確認を中止しました。";
        }
        catch (Exception ex)
        {
            _overlay.Hide();
            StateText.Text = $"画面確認エラー: {ex.Message}";
        }
        finally
        {
            GuideButton.IsEnabled = true;
        }
    }

    private static string DisplayName(string name, string controlType) =>
        string.IsNullOrWhiteSpace(name) ? controlType.Replace("ControlType.", string.Empty) : name;

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        _overlay.Hide();
        StateText.Text = "待機中。必要なときだけ起動します。";
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        _overlay.Hide();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _scanCts?.Cancel();
        _scanCts?.Dispose();
        _hotKey.Dispose();
        _overlay.Close();
    }
}

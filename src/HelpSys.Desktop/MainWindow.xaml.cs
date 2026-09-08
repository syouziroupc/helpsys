using System.ComponentModel;
using System.Net.Http;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using HelpSys.Models;
using HelpSys.Services;

namespace HelpSys;

public partial class MainWindow : Window
{
    private const double MinimumTargetConfidence = 0.72;
    private readonly UiAutomationScanner _scanner = new();
    private readonly GuidePlanner _fallbackPlanner = new();
    private readonly CloudGuideService _cloudGuide = new();
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

    private void OnLoaded(object sender, RoutedEventArgs e) => CollapseToLauncher();

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        _hotKey.Activated += (_, _) => ExpandAssistant();
        if (!_hotKey.Register(hwnd)) StateText.Text = "Ctrl+Alt+H は他のアプリが使用中です。";
    }

    private void PositionNearBottomRight()
    {
        Left = SystemParameters.WorkArea.Right - Width - 18;
        Top = SystemParameters.WorkArea.Bottom - Height - 18;
    }

    private void ExpandAssistant()
    {
        Width = 390;
        Height = 214;
        LauncherButton.Visibility = Visibility.Collapsed;
        AssistantPanel.Visibility = Visibility.Visible;
        PositionNearBottomRight();
        Show();
        WindowState = WindowState.Normal;
        Activate();
        RequestBox.Focus();
        StateText.Text = "何をしたいですか？";
    }

    private void CollapseToLauncher()
    {
        _scanCts?.Cancel();
        _overlay.Hide();
        AssistantPanel.Visibility = Visibility.Collapsed;
        LauncherButton.Visibility = Visibility.Visible;
        Width = 94;
        Height = 58;
        PositionNearBottomRight();
    }

    private void LauncherButton_Click(object sender, RoutedEventArgs e) => ExpandAssistant();
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
        _scanCts = new CancellationTokenSource(TimeSpan.FromSeconds(14));
        var cancellationToken = _scanCts.Token;

        GuideButton.IsEnabled = false;
        _overlay.Hide();
        StateText.Text = "現在の画面を確認しています…";

        try
        {
            var candidates = await _scanner.CaptureCandidatesAsync(360, cancellationToken);
            if (candidates.Count == 0)
            {
                StateText.Text = "操作できる画面要素を取得できませんでした。";
                return;
            }

            StateText.Text = $"{candidates.Count}個の画面要素から次の操作を判断しています…";

            GuideDecision decision;
            try
            {
                decision = await _cloudGuide.PlanAsync(request, candidates, cancellationToken);
            }
            catch (Exception cloudError) when (cloudError is HttpRequestException or TaskCanceledException or InvalidOperationException)
            {
                await RunConservativeFallbackAsync(request, cancellationToken);
                return;
            }

            if (decision.Status.Equals("clarify", StringComparison.OrdinalIgnoreCase))
            {
                StateText.Text = decision.Question ?? "やりたい操作をもう少し具体的に教えてください。";
                return;
            }

            if (!decision.Status.Equals("target", StringComparison.OrdinalIgnoreCase) ||
                decision.Confidence < MinimumTargetConfidence ||
                string.IsNullOrWhiteSpace(decision.TargetId))
            {
                StateText.Text = "今の画面では、次の操作を十分な確度で特定できませんでした。";
                return;
            }

            var target = candidates.FirstOrDefault(x => string.Equals(x.Id, decision.TargetId, StringComparison.Ordinal));
            if (target is null || target.Bounds.IsEmpty)
            {
                StateText.Text = "AIが選んだ対象を現在画面で再確認できませんでした。";
                return;
            }

            var instruction = string.IsNullOrWhiteSpace(decision.Instruction) ? "ここを左クリックしてください。" : decision.Instruction;
            _overlay.ShowTarget(target.Bounds, instruction);
            StateText.Text = $"案内中: {DisplayName(target.Name, target.ControlType)}　確度 {decision.Confidence:P0}";
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

    private async Task RunConservativeFallbackAsync(string request, CancellationToken cancellationToken)
    {
        var plan = _fallbackPlanner.CreateFirstStep(request);
        var target = await _scanner.FindBestTargetAsync(plan.TargetHints, cancellationToken);
        if (target is null || target.Score < 30)
        {
            StateText.Text = "判断APIに接続できず、安全に案内できる対象も特定できませんでした。";
            return;
        }

        _overlay.ShowTarget(target.Bounds, plan.Instruction);
        StateText.Text = $"ローカル案内: {DisplayName(target.Name, target.ControlType)}";
    }

    private static string DisplayName(string name, string controlType) =>
        string.IsNullOrWhiteSpace(name) ? controlType.Replace("ControlType.", string.Empty) : name;

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        _overlay.Hide();
        StateText.Text = "何をしたいですか？";
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => CollapseToLauncher();

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _scanCts?.Cancel();
        _scanCts?.Dispose();
        _cloudGuide.Dispose();
        _hotKey.Dispose();
        _overlay.Close();
    }
}

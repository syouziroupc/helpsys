using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;

namespace HelpSys.Stable;

public partial class MainWindow : Window
{
    private readonly ObservationService _observation = new();
    private readonly GeminiPlannerClient _planner = new();
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private CancellationTokenSource? _runCts;
    private OverlayWindow? _overlay;
    private HwndSource? _source;
    private nint _windowHandle;
    private AppPhase _phase = AppPhase.Idle;
    private bool _closing;

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += MainWindow_SourceInitialized;
        Closing += MainWindow_Closing;
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        _windowHandle = new WindowInteropHelper(this).Handle;
        _source = HwndSource.FromHwnd(_windowHandle);
        _source?.AddHook(WindowProc);

        if (!NativeMethods.RegisterHotKey(_windowHandle, NativeMethods.HotKeyId, NativeMethods.ModNoRepeat, NativeMethods.VkF8))
            SetStatus("F8ショートカットを登録できませんでした。ボタンからは案内できます。");
    }

    private nint WindowProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == NativeMethods.WmHotKey && wParam.ToInt32() == NativeMethods.HotKeyId)
        {
            handled = true;
            _ = RunGuidanceAsync();
        }
        return nint.Zero;
    }

    private async void GuideButton_Click(object sender, RoutedEventArgs e)
        => await RunGuidanceAsync();

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _runCts?.Cancel();
        CloseOverlay();
        _phase = AppPhase.Idle;
        SetStatus("中止しました。HelpSysは操作を実行していません。");
    }

    private async Task RunGuidanceAsync()
    {
        if (_closing) return;

        if (!await _runGate.WaitAsync(0))
        {
            SetStatus("現在の案内処理が完了するまで待ってください。二重実行はしません。");
            return;
        }

        CancellationTokenSource? ownedCts = null;
        try
        {
            var goal = GoalBox.Text.Trim();
            if (goal.Length == 0)
            {
                RestoreMainWindow();
                SetStatus("やりたいことを入力してください。");
                return;
            }

            CloseOverlay();
            _runCts?.Cancel();
            _runCts?.Dispose();
            ownedCts = new CancellationTokenSource();
            _runCts = ownedCts;
            var token = ownedCts.Token;

            _phase = AppPhase.Capturing;
            SetStatus("現在の画面を1回取得しています…");

            if (NativeMethods.GetForegroundWindow() == _windowHandle || IsActive)
            {
                WindowState = WindowState.Minimized;
                await Task.Delay(180, token);
            }

            ScreenObservation observation;
            try
            {
                observation = await _observation.CaptureAsync(token);
            }
            catch (ObservationChangedException)
            {
                // One retry is permitted only when the observed foreground actually changed
                // during capture. This is not a model-confidence retry.
                await Task.Delay(180, token);
                observation = await _observation.CaptureAsync(token);
            }

            _phase = AppPhase.Planning;
            SetStatus("Gemini 3.8 Flashが現在画面から次の1手を判定しています…");
            var plan = await _planner.PlanAsync(goal, observation, token);

            if (!_observation.IsStillCurrent(observation))
                throw new ObservationChangedException("Geminiの応答待ち中に操作対象画面が切り替わったため、古い案内を表示しませんでした。");

            token.ThrowIfCancellationRequested();
            _phase = AppPhase.ShowingResult;

            switch (plan.Status)
            {
                case "target":
                    _overlay = new OverlayWindow(observation, plan);
                    _overlay.Closed += (_, _) => _overlay = null;
                    _overlay.Show();
                    SetStatus(plan.Instruction);
                    WindowState = WindowState.Minimized;
                    break;

                case "clarify":
                    RestoreMainWindow();
                    SetStatus(plan.Question ?? "選択内容を確認してください。");
                    break;

                case "done":
                    RestoreMainWindow();
                    SetStatus(string.IsNullOrWhiteSpace(plan.Instruction)
                        ? "目的の完了を現在画面で確認しました。"
                        : plan.Instruction);
                    break;

                default:
                    throw new PlannerException("Geminiから未知の案内状態が返されました。");
            }
        }
        catch (OperationCanceledException)
        {
            if (!_closing)
            {
                RestoreMainWindow();
                SetStatus("中止しました。HelpSysは操作を実行していません。");
            }
        }
        catch (PrivacyBlockedException ex)
        {
            if (!_closing)
            {
                RestoreMainWindow();
                SetStatus(ex.Message);
            }
        }
        catch (ObservationChangedException ex)
        {
            if (!_closing)
            {
                RestoreMainWindow();
                SetStatus(ex.Message + " 画面が落ち着いた状態でF8を押してください。");
            }
        }
        catch (PlannerException ex)
        {
            if (!_closing)
            {
                RestoreMainWindow();
                SetStatus(ex.Message);
            }
        }
        catch (Exception ex)
        {
            if (!_closing)
            {
                RestoreMainWindow();
                SetStatus("予期しないエラーが発生したため案内を中止しました。操作は実行されていません。 " + ex.Message);
            }
        }
        finally
        {
            _phase = AppPhase.Idle;
            if (!_closing) GuideButton.IsEnabled = true;
            if (ReferenceEquals(_runCts, ownedCts)) _runCts = null;
            ownedCts?.Dispose();
            _runGate.Release();
        }
    }

    private void RestoreMainWindow()
    {
        if (_closing) return;
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void CloseOverlay()
    {
        try { _overlay?.Close(); }
        catch { }
        _overlay = null;
    }

    private void SetStatus(string message)
    {
        if (_closing) return;
        StatusText.Text = message;
        GuideButton.IsEnabled = _phase == AppPhase.Idle || _phase == AppPhase.ShowingResult;
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        _closing = true;
        _runCts?.Cancel();
        CloseOverlay();
        if (_windowHandle != nint.Zero)
            _ = NativeMethods.UnregisterHotKey(_windowHandle, NativeMethods.HotKeyId);
        _source?.RemoveHook(WindowProc);
    }
}

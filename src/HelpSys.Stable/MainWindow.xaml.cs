using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;

namespace HelpSys.Stable;

public partial class MainWindow : Window
{
    private readonly ObservationService _observation = new();
    private readonly GeminiPlannerClient _planner = new();
    private readonly SpeechService _speech = new();
    private readonly UpdateService _updates = new();
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCts = new();
    private CancellationTokenSource? _runCts;
    private CancellationTokenSource? _voiceCts;
    private CancellationTokenSource? _updateCts;
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
        VoiceButton.IsEnabled = _speech.RecognitionAvailable;
        if (!_speech.RecognitionAvailable)
            VoiceButton.ToolTip = "このPCではWindows音声認識を利用できません。";
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        _windowHandle = new WindowInteropHelper(this).Handle;
        _source = HwndSource.FromHwnd(_windowHandle);
        _source?.AddHook(WindowProc);

        var guideRegistered = NativeMethods.RegisterHotKey(_windowHandle, NativeMethods.HotKeyGuideId, NativeMethods.ModNoRepeat, NativeMethods.VkF8);
        var voiceRegistered = NativeMethods.RegisterHotKey(_windowHandle, NativeMethods.HotKeyVoiceId, NativeMethods.ModNoRepeat, NativeMethods.VkF9);
        if (!guideRegistered)
            SetStatus("F8ショートカットを登録できませんでした。ボタンからは案内できます。");
        else if (!voiceRegistered && _speech.RecognitionAvailable)
            SetStatus("F9音声ショートカットを登録できませんでした。音声入力ボタンは利用できます。");
    }

    private nint WindowProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg != NativeMethods.WmHotKey) return nint.Zero;

        if (wParam.ToInt32() == NativeMethods.HotKeyGuideId)
        {
            handled = true;
            _ = RunGuidanceAsync();
        }
        else if (wParam.ToInt32() == NativeMethods.HotKeyVoiceId)
        {
            handled = true;
            _ = StartVoiceInputAsync();
        }

        return nint.Zero;
    }

    private async void GuideButton_Click(object sender, RoutedEventArgs e)
        => await RunGuidanceAsync();

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _runCts?.Cancel();
        _voiceCts?.Cancel();
        _updateCts?.Cancel();
        _speech.StopSpeaking();
        CloseOverlay();
        if (_phase is AppPhase.Capturing or AppPhase.Planning or AppPhase.Validating or AppPhase.Listening or AppPhase.Updating)
            SetStatus("中止要求を受け付けました。現在の処理を安全に終了しています…");
        else
        {
            _phase = AppPhase.Idle;
            SetStatus("中止しました。HelpSysは操作を実行していません。");
        }
    }

    private async void VoiceButton_Click(object sender, RoutedEventArgs e)
        => await StartVoiceInputAsync();

    private async Task StartVoiceInputAsync()
    {
        if (_closing || _phase != AppPhase.Idle || !_speech.RecognitionAvailable) return;
        CancellationTokenSource? ownedCts = null;
        try
        {
            ownedCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
            _voiceCts = ownedCts;
            RestoreMainWindow();
            _phase = AppPhase.Listening;
            SetStatus("8秒以内で、やりたいことを話してください…");
            var text = await _speech.ListenOnceAsync(ownedCts.Token);
            ownedCts.Token.ThrowIfCancellationRequested();
            if (_closing) return;
            _phase = AppPhase.Idle;
            if (string.IsNullOrWhiteSpace(text))
            {
                SetStatus("音声を認識できませんでした。文字入力でも利用できます。");
                return;
            }

            GoalBox.Text = text;
            GoalBox.CaretIndex = GoalBox.Text.Length;
            SetStatus($"「{text}」として認識しました。操作したい画面を前面に出してF8を押してください。");
        }
        catch (OperationCanceledException)
        {
            if (!_closing)
            {
                _phase = AppPhase.Idle;
                SetStatus("音声入力を中止しました。");
            }
        }
        catch (Exception ex)
        {
            if (_closing) return;
            _phase = AppPhase.Idle;
            SetStatus(ex is PlannerException ? ex.Message : "音声入力を開始できませんでした。文字入力を使ってください。");
        }
        finally
        {
            if (ReferenceEquals(_voiceCts, ownedCts)) _voiceCts = null;
            ownedCts?.Dispose();
            if (!_closing) RefreshControls();
        }
    }

    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_closing || _phase != AppPhase.Idle) return;
        CancellationTokenSource? ownedCts = null;
        try
        {
            ownedCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
            _updateCts = ownedCts;
            _phase = AppPhase.Updating;
            SetStatus($"{VersionInfo.ProductName} の更新を確認しています…");
            var update = await _updates.CheckAsync(ownedCts.Token);
            if (_closing) return;

            if (update is null)
            {
                _phase = AppPhase.Idle;
                SetStatus($"{VersionInfo.ProductName} は最新版です。");
                return;
            }

            _phase = AppPhase.Idle;
            var answer = MessageBox.Show(
                $"HelpSys Stable {update.Version} があります。\n\nSHA-256検証後に入れ替えて再起動します。更新しますか？",
                VersionInfo.ProductName,
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);

            if (answer != MessageBoxResult.Yes)
            {
                SetStatus($"更新可能: HelpSys Stable {update.Version}");
                return;
            }

            _phase = AppPhase.Updating;
            SetStatus($"HelpSys Stable {update.Version} をダウンロードして検証しています…");
            await _updates.InstallAsync(update, ownedCts.Token);
            if (_closing) return;
            SetStatus("更新の検証が完了しました。HelpSysを再起動して入れ替えます。");
            Application.Current.Shutdown();
        }
        catch (OperationCanceledException)
        {
            if (!_closing)
            {
                _phase = AppPhase.Idle;
                SetStatus("更新処理を中止しました。現在のHelpSysはそのまま利用できます。");
            }
        }
        catch (Exception ex)
        {
            if (_closing) return;
            _phase = AppPhase.Idle;
            SetStatus(ex is PlannerException
                ? ex.Message
                : "更新確認に失敗しました。現在のHelpSysはそのまま利用できます。");
        }
        finally
        {
            if (ReferenceEquals(_updateCts, ownedCts)) _updateCts = null;
            ownedCts?.Dispose();
            if (!_closing) RefreshControls();
        }
    }

    private async Task RunGuidanceAsync()
    {
        if (_closing) return;

        if (!await _runGate.WaitAsync(0))
        {
            SetStatus("案内処理は1本だけ実行します。現在の処理が終わるまで待ってください。");
            return;
        }

        CancellationTokenSource? ownedCts = null;
        try
        {
            var goal = GoalBox.Text.Trim();
            if (goal.Length == 0)
            {
                RestoreMainWindow();
                _phase = AppPhase.Idle;
                SetStatus("やりたいことを入力してください。");
                return;
            }

            CloseOverlay();
            _speech.StopSpeaking();
            _runCts?.Cancel();
            _runCts?.Dispose();
            ownedCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
            _runCts = ownedCts;
            var token = ownedCts.Token;

            _phase = AppPhase.Capturing;
            SetStatus("現在の画面とWindowsの操作対象を取得しています…");

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
                await Task.Delay(180, token);
                observation = await _observation.CaptureAsync(token);
            }

            _phase = AppPhase.Planning;
            SetStatus("Gemini 3.8 Flashが現在画面から次の1手を判定しています…");
            var plan = await _planner.PlanAsync(goal, observation, token);

            if (!_observation.IsStillCurrent(observation))
                throw new ObservationChangedException("Geminiの応答待ち中に操作対象画面が切り替わったため、古い案内を破棄しました。");

            _phase = AppPhase.Validating;
            SetStatus("案内対象が今も画面上に存在するか最終確認しています…");
            if (!await _observation.IsPlanStillApplicableAsync(observation, plan, token))
                throw new ObservationChangedException("Geminiが選んだ操作対象が応答待ち中に変化したため、古い案内を破棄しました。");

            token.ThrowIfCancellationRequested();
            _phase = AppPhase.ShowingResult;

            switch (plan.Status)
            {
                case "target":
                    _overlay = new OverlayWindow(observation, plan);
                    _overlay.Closed += (_, _) => _overlay = null;
                    _overlay.Show();
                    SetStatus(plan.Instruction);
                    if (SpeakCheckBox.IsChecked == true) _speech.Speak(plan.Instruction);
                    WindowState = WindowState.Minimized;
                    break;

                case "clarify":
                    RestoreMainWindow();
                    var question = plan.Question ?? "選択内容を確認してください。";
                    SetStatus(question);
                    if (SpeakCheckBox.IsChecked == true) _speech.Speak(question);
                    break;

                case "done":
                    RestoreMainWindow();
                    var done = string.IsNullOrWhiteSpace(plan.Instruction)
                        ? "目的の完了を現在画面で確認しました。"
                        : plan.Instruction;
                    SetStatus(done);
                    if (SpeakCheckBox.IsChecked == true) _speech.Speak(done);
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
                SetStatus(ex.Message + " 現在の画面でF8をもう一度押してください。");
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
                App.LogException("guidance_unexpected", ex);
                SetStatus("予期しない内部エラーを処理して案内を中止しました。操作は実行されていません。");
            }
        }
        finally
        {
            _phase = AppPhase.Idle;
            if (!_closing) RefreshControls();
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
        RefreshControls();
    }

    private void RefreshControls()
    {
        if (_closing) return;
        var idle = _phase == AppPhase.Idle;
        GuideButton.IsEnabled = idle || _phase == AppPhase.ShowingResult;
        VoiceButton.IsEnabled = idle && _speech.RecognitionAvailable;
        UpdateButton.IsEnabled = idle;
        GoalBox.IsEnabled = idle || _phase == AppPhase.ShowingResult;
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        _closing = true;
        try { _lifetimeCts.Cancel(); } catch { }
        try { _runCts?.Cancel(); } catch { }
        try { _voiceCts?.Cancel(); } catch { }
        try { _updateCts?.Cancel(); } catch { }
        _speech.StopSpeaking();
        CloseOverlay();
        if (_windowHandle != nint.Zero)
            _ = NativeMethods.UnregisterHotKey(_windowHandle, NativeMethods.HotKeyGuideId);
        if (_windowHandle != nint.Zero)
            _ = NativeMethods.UnregisterHotKey(_windowHandle, NativeMethods.HotKeyVoiceId);
        _source?.RemoveHook(WindowProc);
        try { _speech.Dispose(); } catch { }
        try { _planner.Dispose(); } catch { }
        try { _updates.Dispose(); } catch { }
    }
}

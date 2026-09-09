using System.ComponentModel;
using System.Windows;
using HelpSys.Services;

namespace HelpSys;

public partial class MainWindow
{
    private readonly CommanderWakeService _commander = new();
    private int _commanderWakeBusy;
    private bool _commanderStarted;

    private void MainWindow_CombinedLoaded(object sender, RoutedEventArgs e)
    {
        MainWindow_ReliabilityV3Loaded(sender, e);
        if (_commanderStarted) return;
        _commanderStarted = true;
        _commander.WakeDetected += Commander_WakeDetected;
        _commander.StatusChanged += Commander_StatusChanged;
        _commander.Start();
        UpdateCommanderUi();
    }

    private void MainWindow_CombinedClosing(object? sender, CancelEventArgs e)
    {
        if (_commanderStarted)
        {
            _commanderStarted = false;
            _commander.WakeDetected -= Commander_WakeDetected;
            _commander.StatusChanged -= Commander_StatusChanged;
        }
        _commander.Dispose();
        MainWindow_ReliabilityV3Closing(sender, e);
    }

    private void CommanderButton_Click(object sender, RoutedEventArgs e)
    {
        _commander.SetEnabled(!_commander.Enabled);
        UpdateCommanderUi();
        SetState(_commander.Enabled
            ? "コマンダーを有効にしました。「ねえコマンダー」で呼び出せます。"
            : "コマンダーを停止しました。マイク待機も解除しています。", speak: false);
    }

    private void Commander_StatusChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        Dispatcher.BeginInvoke(new Action(UpdateCommanderUi));
    }

    private void UpdateCommanderUi()
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished || CommanderButton is null) return;
        CommanderButton.Content = !_commander.Enabled
            ? "コマンダー OFF"
            : _commander.Listening ? "コマンダー ●" : "コマンダー";
        CommanderButton.ToolTip = !_commander.Enabled
            ? "押すと『ねえコマンダー』の待機を開始します"
            : $"『ねえコマンダー』で呼び出す / 状態: {_commander.Status}";
    }

    private void Commander_WakeDetected(object? sender, EventArgs e)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            _commander.CompleteWakeInteraction();
            return;
        }

        Dispatcher.BeginInvoke(new Action(async () => await HandleCommanderWakeAsync()));
    }

    private async Task HandleCommanderWakeAsync()
    {
        if (Interlocked.Exchange(ref _commanderWakeBusy, 1) != 0)
        {
            _commander.CompleteWakeInteraction();
            return;
        }

        CancellationTokenSource? localVoiceCts = null;
        string? command = null;
        try
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            ExpandAssistant();

            if (_planning || _verifyingAction)
            {
                SetState("今の案内を確認中です。終わったらもう一度「ねえコマンダー」と呼んでください。", speak: true);
                return;
            }

            _speechOutput.Stop();
            SetState("コマンダーを呼び出しました。用件を話してください。", speak: false);
            _speechOutput.Speak("はい。どうしましたか？", allowRepeat: true);

            // The wake recognizer is already fully released. Keep it suspended while the
            // spoken acknowledgement is playing so it cannot hear itself, then acquire the
            // microphone for one normal dictation turn.
            await Task.Delay(2300);

            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            if (_voiceCts is not null)
            {
                // A user may manually start voice input during the acknowledgement. Never
                // steal the same microphone; foreground manual capture keeps priority.
                SetState("別の音声入力が動いているため、コマンダーはマイクを取りません。", speak: false);
                return;
            }

            localVoiceCts = new CancellationTokenSource();
            _voiceCts = localVoiceCts;
            VoiceButton.Content = "停止";
            GuideButton.IsEnabled = false;
            SetState("コマンダーが用件を聞いています…", speak: false);

            command = await _speechInput.RecognizeOnceAsync(localVoiceCts.Token);
            if (string.IsNullOrWhiteSpace(command) && !localVoiceCts.IsCancellationRequested &&
                !Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
                SetState("用件を聞き取れませんでした。もう一度「ねえコマンダー」と呼ぶか、文字で入力してください。", speak: true);
        }
        catch (OperationCanceledException)
        {
            if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
                SetState("コマンダーの音声入力を停止しました。", speak: false);
        }
        catch (Exception ex)
        {
            if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
                SetState($"コマンダーの音声入力を開始できません: {ex.Message}", speak: true);
        }
        finally
        {
            if (ReferenceEquals(_voiceCts, localVoiceCts)) _voiceCts = null;
            try { localVoiceCts?.Dispose(); } catch { }
            _commander.CompleteWakeInteraction();
            Interlocked.Exchange(ref _commanderWakeBusy, 0);

            if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
            {
                VoiceButton.Content = "音声";
                GuideButton.IsEnabled = !_planning;
                UpdateCommanderUi();
            }
        }

        if (string.IsNullOrWhiteSpace(command) || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        RequestBox.Text = command.Trim();
        RequestBox.CaretIndex = RequestBox.Text.Length;
        SetState($"コマンダー: 「{RequestBox.Text}」を確認しました。画面を見て案内を作ります…", speak: false);
        await StartOrContinueSessionAsync();
    }
}

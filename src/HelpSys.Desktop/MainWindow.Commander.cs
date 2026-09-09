using System.ComponentModel;
using System.Windows;
using HelpSys.Services;

namespace HelpSys;

public partial class MainWindow
{
    private readonly CommanderWakeService _commander = new();
    private int _commanderWakeBusy;
    private bool _commanderStarted;
    private CancellationTokenSource? _commanderInteractionCts;

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
        try { _commanderInteractionCts?.Cancel(); } catch { }
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
        var enable = !_commander.Enabled;
        if (!enable)
        {
            try { _commanderInteractionCts?.Cancel(); } catch { }
        }
        _commander.SetEnabled(enable);
        UpdateCommanderUi();
        SetState(enable
            ? "コマンダーを有効にしました。音声で呼び出せます。"
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
            ? "押すと音声呼び出しの待機を開始します"
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

        CancellationTokenSource? interactionCts = null;
        CancellationTokenSource? localVoiceCts = null;
        try
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            ExpandAssistant();

            if (_planning || _verifyingAction)
            {
                SetState("現在の案内を確認中です。処理が終わってから呼び出してください。", speak: false);
                return;
            }

            interactionCts = new CancellationTokenSource(TimeSpan.FromSeconds(16));
            _commanderInteractionCts = interactionCts;
            var token = interactionCts.Token;

            _speechOutput.Stop();
            SetState("コマンダーを呼び出しました。用件を話してください。", speak: false);

            // Do not guess how long TTS takes. Dictation starts only after the short
            // acknowledgement has actually finished (or its bounded timeout expires).
            await _speechOutput.SpeakPromptAsync("はい。どうしましたか？", token);
            token.ThrowIfCancellationRequested();

            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            if (_voiceCts is not null)
            {
                SetState("別の音声入力が動いているため、コマンダーはマイクを取りません。", speak: false);
                return;
            }

            localVoiceCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            _voiceCts = localVoiceCts;
            VoiceButton.Content = "停止";
            GuideButton.IsEnabled = false;
            SetState("用件を聞いています…", speak: false);

            var command = await _speechInput.RecognizeOnceAsync(localVoiceCts.Token);
            token.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(command))
            {
                SetState("聞き取れませんでした。必要ならもう一度呼び出してください。", speak: true);
                return;
            }

            RequestBox.Text = command.Trim();
            RequestBox.CaretIndex = RequestBox.Text.Length;
            SetState($"「{RequestBox.Text}」を確認しました。画面を見て案内を作ります…", speak: false);

            // Keep the wake recognizer released through the initial planning pass. This avoids
            // a new wake callback racing the same microphone/UI while the current request starts.
            await StartOrContinueSessionAsync();
        }
        catch (OperationCanceledException)
        {
            if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
                SetState("音声入力を終了しました。", speak: false);
        }
        catch (Exception ex)
        {
            if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
                SetState($"音声入力を開始できません: {ex.Message}", speak: false);
        }
        finally
        {
            if (ReferenceEquals(_voiceCts, localVoiceCts)) _voiceCts = null;
            try { localVoiceCts?.Dispose(); } catch { }

            if (ReferenceEquals(_commanderInteractionCts, interactionCts)) _commanderInteractionCts = null;
            try { interactionCts?.Dispose(); } catch { }

            _commander.CompleteWakeInteraction();
            Interlocked.Exchange(ref _commanderWakeBusy, 0);

            if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
            {
                VoiceButton.Content = "音声";
                GuideButton.IsEnabled = !_planning;
                UpdateCommanderUi();
            }
        }
    }
}

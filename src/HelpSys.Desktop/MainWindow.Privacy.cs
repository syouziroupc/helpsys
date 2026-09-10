using System.Windows;
using HelpSys.Models;
using HelpSys.Services;

namespace HelpSys;

public partial class MainWindow
{
    private bool _privacyPaused;
    private int _privacyPulseBusy;

    private void MainWindow_PrivacyLoaded(object sender, RoutedEventArgs e)
    {
        MainWindow_CombinedLoaded(sender, e);
        _cloudGuide.PrivacyBlocked += CloudGuide_PrivacyBlocked;
        _liveWatcher.Pulse += PrivacyWatcher_Pulse;
        UpdatePrivacyButton();

        if (!_speechInput.CloudTranscriptionAllowed)
        {
            try { _commander.SetEnabled(false); } catch { }
            VoiceButton.IsEnabled = false;
            VoiceButton.ToolTip = "安全版では音声データをクラウドへ送信しません。文字入力を使用してください。";
            CommanderButton.IsEnabled = false;
            CommanderButton.Content = "コマンダー OFF";
            CommanderButton.ToolTip = "安全版ではクラウド音声認識を使用しないため無効です。";
        }

        SetState(
            _cloudGuide.PrivacyGate.Profile == PrivacyPolicyProfile.Safe
                ? "安全版：秘密情報の画面では送信を止めます。音声のクラウド送信も無効です。画面解析はいつでも停止できます。"
                : "画面情報を利用して案内します。秘密情報の画面では送信を止めます。画面解析はいつでも停止できます。",
            speak: false);
    }

    private void MainWindow_PrivacyClosed(object? sender, EventArgs e)
    {
        _cloudGuide.PrivacyBlocked -= CloudGuide_PrivacyBlocked;
        _liveWatcher.Pulse -= PrivacyWatcher_Pulse;
    }

    private void CloudGuide_PrivacyBlocked(PrivacyAssessment assessment)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        if (Dispatcher.CheckAccess()) EnterPrivacyMode(assessment);
        else Dispatcher.Invoke(() => EnterPrivacyMode(assessment));
    }

    private void EnterPrivacyMode(PrivacyAssessment assessment)
    {
        _privacyPaused = true;
        _sessionState.Invalidate(GuidanceSessionState.Idle);
        _speechOutput.Stop();
        _overlay.Hide();
        _keyHint.Hide();
        _currentDecision = null;
        _currentTarget = null;
        _guidedBounds = null;
        _stepBaseline = [];
        _stepSystemBaseline = null;
        _actionObserver.Stop();
        _liveReplanPending = false;
        _liveRestartAfterPlanCancel = false;
        try { _sessionCts?.Cancel(); } catch { }
        UpdatePrivacyButton();
        SetState($"プライバシー保護のため画面解析を一時停止中。{assessment.UserMessage}", speak: false);
    }

    private async void PrivacyButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_cloudGuide.PrivacyGate.ManualPause)
        {
            _cloudGuide.PrivacyGate.SetManualPause(true);
            EnterPrivacyMode(new PrivacyAssessment(
                PrivacyClassification.ManualPause,
                "manual_pause",
                "利用者の操作で画面解析を停止しました。"));
            return;
        }

        _cloudGuide.PrivacyGate.SetManualPause(false);
        _privacyPaused = true;
        UpdatePrivacyButton();
        SetState("画面解析を再開できる安全な状態か確認しています…", speak: false);
        await TryResumePrivacyModeAsync();
    }

    private void PrivacyWatcher_Pulse(object? sender, EventArgs e)
    {
        if (!_privacyPaused || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        if (Interlocked.Exchange(ref _privacyPulseBusy, 1) != 0) return;

        try
        {
            Dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await TryResumePrivacyModeAsync(); }
                catch { }
                finally { Interlocked.Exchange(ref _privacyPulseBusy, 0); }
            }));
        }
        catch
        {
            Interlocked.Exchange(ref _privacyPulseBusy, 0);
        }
    }

    private async Task TryResumePrivacyModeAsync()
    {
        if (!_privacyPaused || _cloudGuide.PrivacyGate.ManualPause) return;
        if (_activeRequest is null)
        {
            _privacyPaused = false;
            UpdatePrivacyButton();
            SetState("画面解析を再開しました。やりたいことを入力してください。", speak: false);
            return;
        }

        var context = _systemContext.Capture();
        if (!HasUsableForeground(context)) return;

        IReadOnlyList<UiElementCandidate> candidates;
        try
        {
            await _liveWatcher.SetForegroundProcessAsync(context.ForegroundProcessId, CancellationToken.None);
            candidates = await _scanner.CaptureCandidatesForProcessAsync(context.ForegroundProcessId, 220, CancellationToken.None);
        }
        catch
        {
            return;
        }

        var assessment = _cloudGuide.PrivacyGate.EvaluateState(context, candidates);
        if (!assessment.CanSend)
        {
            SetState($"プライバシー保護のため画面解析を一時停止中。{assessment.UserMessage}", speak: false);
            return;
        }

        _privacyPaused = false;
        var oldCts = _sessionCts;
        _sessionCts = new CancellationTokenSource();
        try { oldCts?.Dispose(); } catch { }
        _sessionState.Invalidate(GuidanceSessionState.Idle);
        _liveElements = [];
        _liveSystem = null;
        try { _actionObserver.Start(); } catch { }
        UpdatePrivacyButton();
        SetState("安全な画面に戻ったため、画面解析を再開します…", speak: false);
        await AdvanceGuideAsync();
    }

    private void UpdatePrivacyButton()
    {
        if (PrivacyButton is null) return;
        PrivacyButton.Content = _cloudGuide.PrivacyGate.ManualPause ? "解析再開" : "解析停止";
        PrivacyButton.ToolTip = _cloudGuide.PrivacyGate.ManualPause
            ? "画面解析の再開を試みます"
            : "画面解析を停止します";
    }
}

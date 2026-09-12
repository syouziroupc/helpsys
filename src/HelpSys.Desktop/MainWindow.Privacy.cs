using System.Windows;
using System.Windows.Threading;
using HelpSys.Models;
using HelpSys.Services;

namespace HelpSys;

public partial class MainWindow
{
    private bool _privacyPaused;
    private int _privacyPulseBusy;
    private readonly DispatcherTimer _privacyResumeTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(900)
    };

    private void MainWindow_PrivacyLoaded(object sender, RoutedEventArgs e)
    {
        MainWindow_CombinedLoaded(sender, e);
        _cloudGuide.PrivacyBlocked += CloudGuide_PrivacyBlocked;
        _liveWatcher.Pulse += PrivacyWatcher_Pulse;
        _privacyResumeTimer.Tick += PrivacyResumeTimer_Tick;
        UpdatePrivacyButton();
        Dispatcher.BeginInvoke(new Action(SetPrivacyAwareStartupState));
    }

    private void SetPrivacyAwareStartupState()
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;

        if (!_cloudGuide.CloudEndpointConfigured)
        {
            SetState("AI接続先を確認できないため、外部送信を停止しています。", speak: false);
            return;
        }

        SetState(
            "HelpSys 統合版：Privacy Gateで秘密情報を遮断し、安全な画面だけを解析します。音声入力は利用者が開始したときだけ送信します。",
            speak: false);
    }

    private void MainWindow_PrivacyClosed(object? sender, EventArgs e)
    {
        _privacyResumeTimer.Stop();
        _privacyResumeTimer.Tick -= PrivacyResumeTimer_Tick;
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

        try { _sessionCts?.Cancel(); } catch { }
        _sessionState.AbortCurrentOperation(GuidanceSessionState.Idle);

        _speechOutput.Stop();
        PrivacySentinel_SuspendCloudAudio();
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

        if (assessment.Classification == PrivacyClassification.ManualPause || !_cloudGuide.CloudEndpointConfigured)
            _privacyResumeTimer.Stop();
        else
            _privacyResumeTimer.Start();

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
        if (_cloudGuide.CloudEndpointConfigured) _privacyResumeTimer.Start();
        UpdatePrivacyButton();
        SetState("画面解析を再開できる安全な状態か確認しています…", speak: false);
        await TryResumePrivacyModeAsync();
    }

    private void PrivacyWatcher_Pulse(object? sender, EventArgs e)
    {
        QueuePrivacyResumeCheck();
    }

    private void PrivacyResumeTimer_Tick(object? sender, EventArgs e)
    {
        QueuePrivacyResumeCheck();
    }

    private void QueuePrivacyResumeCheck()
    {
        if (!_privacyPaused || _cloudGuide.PrivacyGate.ManualPause ||
            Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
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

        if (!_cloudGuide.CloudEndpointConfigured)
        {
            _privacyResumeTimer.Stop();
            SetState("画面解析を停止中。AI接続先を安全に確認できません。", speak: false);
            return;
        }

        var context = _systemContext.Capture();
        if (!HasUsableForeground(context)) return;

        var contextAssessment = _cloudGuide.PreflightPrivacy(context, Array.Empty<UiElementCandidate>());
        if (!contextAssessment.CanSend)
        {
            SetState($"プライバシー保護のため画面解析を一時停止中。{contextAssessment.UserMessage}", speak: false);
            return;
        }

        PrivacySentinel_RestorePendingInputForResume();

        if (_activeRequest is null)
        {
            _privacyPaused = false;
            _privacyResumeTimer.Stop();
            UpdatePrivacyButton();
            PrivacySentinel_RestoreCloudAudio();
            SetState("画面解析を再開しました。やりたいことを入力してください。", speak: false);
            return;
        }

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

        var freshContext = _systemContext.Capture();
        if (freshContext.ForegroundProcessId != context.ForegroundProcessId ||
            freshContext.ForegroundWindowHandle != context.ForegroundWindowHandle)
            return;

        var assessment = _cloudGuide.PrivacyGate.EvaluateState(freshContext, candidates);
        if (!assessment.CanSend)
        {
            SetState($"プライバシー保護のため画面解析を一時停止中。{assessment.UserMessage}", speak: false);
            return;
        }

        var oldCts = _sessionCts;
        try { oldCts?.Cancel(); } catch { }
        _sessionState.AbortCurrentOperation(GuidanceSessionState.Idle);
        _sessionCts = new CancellationTokenSource();
        try { oldCts?.Dispose(); } catch { }

        _privacyPaused = false;
        _privacyResumeTimer.Stop();
        _liveElements = [];
        _liveSystem = null;
        try { _actionObserver.Start(); } catch { }
        UpdatePrivacyButton();
        PrivacySentinel_RestoreCloudAudio();
        SetState("安全な画面に戻ったため、画面解析を再開します…", speak: false);
        await AdvanceGuideAsync();
    }

    private void UpdatePrivacyButton()
    {
        if (PrivacyButton is null) return;

        if (_cloudGuide.PrivacyGate.ManualPause)
        {
            PrivacyButton.Content = "解析再開";
            PrivacyButton.ToolTip = "画面解析の再開を試みます";
            return;
        }

        if (_privacyPaused)
        {
            PrivacyButton.Content = "停止を固定";
            PrivacyButton.ToolTip = "秘密情報を検出して自動停止中です。押すと手動停止に切り替えます。";
            return;
        }

        PrivacyButton.Content = "解析停止";
        PrivacyButton.ToolTip = "画面解析を停止します";
    }
}

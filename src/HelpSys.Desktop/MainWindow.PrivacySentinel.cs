using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using HelpSys.Models;
using HelpSys.Services;

namespace HelpSys;

public partial class MainWindow
{
    private const uint PrivacySentinelEventSystemForeground = 0x0003;
    private const uint PrivacySentinelOutOfContext = 0x0000;
    private const uint PrivacySentinelSkipOwnProcess = 0x0002;

    private nint _privacySentinelHook;
    private PrivacySentinelWinEventProc? _privacySentinelCallback;
    private int _privacySentinelBusy;
    private bool _privacySentinelRestoreCommander;
    private string? _privacySentinelPendingInput;
    private bool _privacySentinelPendingClarification;

    static MainWindow()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(PrivacySentinel_Loaded),
            true);
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            FrameworkElement.UnloadedEvent,
            new RoutedEventHandler(PrivacySentinel_Unloaded),
            true);
        EventManager.RegisterClassHandler(
            typeof(Button),
            Button.ClickEvent,
            new RoutedEventHandler(PrivacySentinel_ButtonClick),
            true);
        EventManager.RegisterClassHandler(
            typeof(TextBox),
            Keyboard.KeyDownEvent,
            new KeyEventHandler(PrivacySentinel_TextBoxKeyDown),
            true);
    }

    private static void PrivacySentinel_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not MainWindow window || window._privacySentinelHook != nint.Zero) return;

        window._privacySentinelCallback = window.PrivacySentinel_ForegroundChanged;
        window._privacySentinelHook = SetWinEventHook(
            PrivacySentinelEventSystemForeground,
            PrivacySentinelEventSystemForeground,
            nint.Zero,
            window._privacySentinelCallback,
            0,
            0,
            PrivacySentinelOutOfContext | PrivacySentinelSkipOwnProcess);

        // A failed hook must never weaken the per-request Privacy Gate. It only removes the
        // proactive transition detector; every cloud-bound path is still gated independently.
    }

    private static void PrivacySentinel_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not MainWindow window) return;
        var hook = window._privacySentinelHook;
        window._privacySentinelHook = nint.Zero;
        window._privacySentinelCallback = null;
        if (hook != nint.Zero)
        {
            try { UnhookWinEvent(hook); } catch { }
        }
    }

    private static void PrivacySentinel_ButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || Window.GetWindow(button) is not MainWindow window) return;

        // Starting guidance is local work: establish the request, inspect UIA locally, then let the
        // mandatory per-egress Privacy Gate decide whether any structured data or screenshot may
        // leave the machine. Blocking the routed Click here made a transient foreground race look
        // like a dead button even though no cloud egress had been attempted yet.
        if (button.Name is "GuideButton" or "AnswerButton")
        {
            window.PrivacySentinel_ClearPendingInput();
            return;
        }

        var startsCloudWork = false;
        if (button.Name is "VoiceButton" or "AnswerVoiceButton")
            startsCloudWork = window._voiceCts is null;
        if (button.Name == "CommanderButton")
            startsCloudWork = !window._commander.Enabled;

        if (!startsCloudWork) return;
        if (window.PrivacySentinel_AllowCloudAction()) return;

        // Cloud audio can begin directly from these controls, so an unsafe/unknown foreground still
        // blocks the action before microphone data can reach cloud transcription.
        e.Handled = true;
    }

    private static void PrivacySentinel_TextBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox box || Window.GetWindow(box) is not MainWindow window) return;
        if (box.Name is not ("RequestBox" or "AnswerBox")) return;

        // Enter starts the same local guidance path as GuideButton. Do not swallow it here; all
        // actual cloud-bound guidance paths are independently gated immediately before egress.
        window.PrivacySentinel_ClearPendingInput();
    }

    private void PrivacySentinel_StagePendingInput(TextBox source)
    {
        var text = source.Text.Trim();
        if (text.Length == 0) return;
        _privacySentinelPendingInput = text;
        _privacySentinelPendingClarification = source.Name == "AnswerBox" || _awaitingClarification;
    }

    private void PrivacySentinel_ClearPendingInput()
    {
        _privacySentinelPendingInput = null;
        _privacySentinelPendingClarification = false;
    }

    private bool PrivacySentinel_RestorePendingInputForResume()
    {
        var pending = _privacySentinelPendingInput;
        if (string.IsNullOrWhiteSpace(pending)) return _activeRequest is not null;

        var clarification = _privacySentinelPendingClarification;
        PrivacySentinel_ClearPendingInput();

        if (clarification && _activeRequest is not null)
        {
            _history.Add(new GuideHistoryItem(
                _stepNumber,
                "clarification_answer",
                pending,
                _clarificationQuestion ?? "確認質問"));
            if (_history.Count > 12) _history.RemoveAt(0);
            _activeRequest += $"\n利用者からの追加回答: {pending}";
            _clarificationQuestion = null;
            GuideButton.Content = "案内";
            RequestBox.Text = _originalRequest ?? _activeRequest;
            RequestBox.CaretIndex = RequestBox.Text.Length;
            ClarificationPanel.Visibility = Visibility.Collapsed;
            AnswerBox.Clear();
            return true;
        }

        EndSession();
        _originalRequest = pending;
        _activeRequest = pending;
        _stepNumber = 0;
        _history.Clear();
        _clarificationQuestion = null;
        GuideButton.Content = "案内";
        RequestBox.Text = pending;
        RequestBox.CaretIndex = RequestBox.Text.Length;
        return true;
    }

    private bool PrivacySentinel_AllowCloudAction()
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return false;

        var context = _systemContext.Capture();
        var assessment = _cloudGuide.PreflightPrivacy(context, Array.Empty<UiElementCandidate>());
        if (assessment.CanSend) return true;

        PrivacySentinel_SuspendCloudAudio();
        return false;
    }

    private void PrivacySentinel_ForegroundChanged(
        nint hook,
        uint eventType,
        nint hwnd,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime)
    {
        if (hwnd == nint.Zero || eventType != PrivacySentinelEventSystemForeground) return;
        if (_activeRequest is null && string.IsNullOrWhiteSpace(_privacySentinelPendingInput) &&
            !_privacyPaused && _voiceCts is null && _commanderInteractionCts is null) return;
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        if (Interlocked.Exchange(ref _privacySentinelBusy, 1) != 0) return;

        try
        {
            Dispatcher.BeginInvoke(new Action(async () =>
            {
                try
                {
                    // Foreground hooks from different processes can arrive a few tens of milliseconds
                    // apart. Give SystemContextService a small settling window before comparing HWNDs.
                    await Task.Delay(70);
                    await PrivacySentinel_CheckForegroundAsync(hwnd);
                }
                catch { }
                finally { Interlocked.Exchange(ref _privacySentinelBusy, 0); }
            }));
        }
        catch
        {
            Interlocked.Exchange(ref _privacySentinelBusy, 0);
        }
    }

    private async Task PrivacySentinel_CheckForegroundAsync(nint eventWindow)
    {
        if (_cloudGuide.PrivacyGate.ManualPause) return;
        if (eventWindow == nint.Zero) return;

        GetWindowThreadProcessId(eventWindow, out var rawEventPid);
        var eventPid = unchecked((int)rawEventPid);
        var context = _systemContext.Capture();

        if (!PrivacySentinel_MatchesForeground(eventPid, eventWindow, context))
        {
            // Stop cloud audio immediately while the desktop is unsettled, but do not kill a
            // guidance session on one transient HWND mismatch. Screen/structured egress remains
            // fail-closed because every send path runs PrivacyGate and an identity check again.
            PrivacySentinel_SuspendCloudAudio();

            await Task.Delay(140);
            context = _systemContext.Capture();

            // A newer external foreground can legitimately supersede the event we are handling.
            // Treat the old event as stale, then validate the current foreground normally.
            if (HasUsableForeground(context) && context.ForegroundWindowHandle != eventWindow)
            {
                var currentAssessment = _cloudGuide.PreflightPrivacy(context, Array.Empty<UiElementCandidate>());
                if (currentAssessment.CanSend)
                {
                    if (_privacyPaused) await TryResumePrivacyModeAsync();
                    if (!_privacyPaused) PrivacySentinel_RestoreCloudAudio();
                }
                return;
            }

            if (!PrivacySentinel_MatchesForeground(eventPid, eventWindow, context))
            {
                await Task.Delay(180);
                context = _systemContext.Capture();
            }

            if (!PrivacySentinel_MatchesForeground(eventPid, eventWindow, context))
            {
                EnterPrivacyMode(new PrivacyAssessment(
                    PrivacyClassification.Unknown,
                    "foreground_transition_unverified",
                    "画面切替の確認が安定しないため、クラウド画面解析を一時停止しています。"));
                return;
            }
        }

        var assessment = _cloudGuide.PreflightPrivacy(context, Array.Empty<UiElementCandidate>());
        if (!assessment.CanSend)
        {
            PrivacySentinel_SuspendCloudAudio();
            return;
        }

        if (_privacyPaused)
        {
            await TryResumePrivacyModeAsync();
            if (!_privacyPaused) PrivacySentinel_RestoreCloudAudio();
        }
    }

    private static bool PrivacySentinel_MatchesForeground(int eventPid, nint eventWindow, SystemContextSnapshot context) =>
        eventPid > 0 &&
        HasUsableForeground(context) &&
        context.ForegroundProcessId == eventPid &&
        context.ForegroundWindowHandle == eventWindow;

    private void PrivacySentinel_SuspendCloudAudio()
    {
        try { _voiceCts?.Cancel(); } catch { }
        try { _commanderInteractionCts?.Cancel(); } catch { }

        if (_commander.Enabled)
        {
            _privacySentinelRestoreCommander = true;
            try { _commander.SetEnabled(false); } catch { }
            try { UpdateCommanderUi(); } catch { }
        }
    }

    private void PrivacySentinel_RestoreCloudAudio()
    {
        if (!_privacySentinelRestoreCommander) return;
        _privacySentinelRestoreCommander = false;
        if (!_speechInput.CloudTranscriptionAllowed) return;

        try { _commander.SetEnabled(true); } catch { }
        try { UpdateCommanderUi(); } catch { }
    }

    private delegate void PrivacySentinelWinEventProc(
        nint hook,
        uint eventType,
        nint hwnd,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime);

    [DllImport("user32.dll")]
    private static extern nint SetWinEventHook(
        uint eventMin,
        uint eventMax,
        nint eventHookAssembly,
        PrivacySentinelWinEventProc eventProc,
        uint processId,
        uint threadId,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(nint hook);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
}

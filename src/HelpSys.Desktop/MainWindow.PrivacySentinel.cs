using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using HelpSys.Models;

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

        var startsCloudWork = button.Name is "GuideButton" or "AnswerButton";
        if (button.Name is "VoiceButton" or "AnswerVoiceButton")
            startsCloudWork = window._voiceCts is null;
        if (button.Name == "CommanderButton")
            startsCloudWork = !window._commander.Enabled;

        if (!startsCloudWork) return;
        if (window.PrivacySentinel_AllowCloudAction()) return;

        // Class handlers run before the XAML instance Click handler. Marking the routed event
        // handled prevents the original handler from starting UIA scanning, microphone capture or
        // cloud work on a known-dangerous/unknown screen.
        e.Handled = true;
    }

    private static void PrivacySentinel_TextBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox box || Window.GetWindow(box) is not MainWindow window) return;
        if (box.Name is not ("RequestBox" or "AnswerBox")) return;
        if (window.PrivacySentinel_AllowCloudAction()) return;
        e.Handled = true;
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
        if (_activeRequest is null && !_privacyPaused && _voiceCts is null && _commanderInteractionCts is null) return;
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        if (Interlocked.Exchange(ref _privacySentinelBusy, 1) != 0) return;

        try
        {
            Dispatcher.BeginInvoke(new Action(async () =>
            {
                try
                {
                    // SystemContextService has its own foreground hook. Yield briefly so both hooks
                    // settle on the same HWND; disagreement is treated as UNKNOWN below.
                    await Task.Delay(45);
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

        if (eventPid <= 0 || !HasUsableForeground(context) || context.ForegroundProcessId != eventPid)
        {
            PrivacySentinel_SuspendCloudAudio();
            EnterPrivacyMode(new PrivacyAssessment(
                PrivacyClassification.Unknown,
                "foreground_transition_unverified",
                "画面切替を安全に照合できないため、クラウド画面解析を停止しています。"));
            return;
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

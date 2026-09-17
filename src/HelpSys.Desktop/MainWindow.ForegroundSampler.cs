using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace HelpSys;

public partial class MainWindow
{
    private static readonly bool ForegroundSamplerClassHandlersRegistered = RegisterForegroundSamplerClassHandlers();

    private static bool RegisterForegroundSamplerClassHandlers()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(BackgroundForegroundSampler_Loaded),
            true);
        EventManager.RegisterClassHandler(
            typeof(Button),
            Button.ClickEvent,
            new RoutedEventHandler(BackgroundForegroundSampler_ButtonClick),
            true);
        EventManager.RegisterClassHandler(
            typeof(TextBox),
            Keyboard.KeyDownEvent,
            new KeyEventHandler(BackgroundForegroundSampler_TextBoxKeyDown),
            true);
        return true;
    }

    private static void BackgroundForegroundSampler_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not MainWindow window) return;

        // Cloud send-time TOCTOU validation must use the same verified/pinned work surface as
        // the UI scanner and screenshot pipeline. Foreground tracking itself is event-driven in
        // SystemContextService; do not poll GetForegroundWindow continuously.
        window._cloudGuide.UseContextVerifier(window._systemContext);
    }

    private static void BackgroundForegroundSampler_ButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || Window.GetWindow(button) is not MainWindow window) return;
        if (button.Name is not ("GuideButton" or "AnswerButton")) return;
        window._systemContext.CommitStableForegroundForAssistantInteraction();
    }

    private static void BackgroundForegroundSampler_TextBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox box || Window.GetWindow(box) is not MainWindow window) return;
        if (box.Name is not ("RequestBox" or "AnswerBox")) return;
        window._systemContext.CommitStableForegroundForAssistantInteraction();
    }
}

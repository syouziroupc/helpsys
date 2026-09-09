using System.Windows.Threading;
using HelpSys.Services;

namespace HelpSys;

public partial class MainWindow
{
    private readonly ListeningOverlayWindow _speechOverlay = new();
    private DispatcherTimer? _speechOverlayHideTimer;

    private void InitializeSpeechOverlay()
    {
        _speechInput.ProgressChanged += SpeechInput_ProgressChanged;
    }

    private void SpeechInput_ProgressChanged(object? sender, SpeechInputProgressEventArgs e)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _speechOverlayHideTimer?.Stop();
            switch (e.Stage)
            {
                case "listening":
                    _speechOverlay.ShowListening(e.Level);
                    break;
                case "transcribing":
                    _speechOverlay.ShowTranscribing();
                    break;
                case "transcribed":
                    _speechOverlay.ShowTranscript(e.Text ?? string.Empty);
                    ScheduleSpeechOverlayHide(TimeSpan.FromSeconds(1.8));
                    break;
                default:
                    _speechOverlay.ShowTranscript(e.Message);
                    ScheduleSpeechOverlayHide(TimeSpan.FromSeconds(1.2));
                    break;
            }
        }));
    }

    private void ScheduleSpeechOverlayHide(TimeSpan delay)
    {
        _speechOverlayHideTimer ??= new DispatcherTimer();
        _speechOverlayHideTimer.Stop();
        _speechOverlayHideTimer.Interval = delay;
        _speechOverlayHideTimer.Tick -= SpeechOverlayHideTimer_Tick;
        _speechOverlayHideTimer.Tick += SpeechOverlayHideTimer_Tick;
        _speechOverlayHideTimer.Start();
    }

    private void SpeechOverlayHideTimer_Tick(object? sender, EventArgs e)
    {
        _speechOverlayHideTimer?.Stop();
        HideSpeechOverlay();
    }

    private void HideSpeechOverlay()
    {
        _speechOverlayHideTimer?.Stop();
        if (_speechOverlay.IsVisible) _speechOverlay.Hide();
    }

    private void ShutdownSpeechOverlay()
    {
        _speechInput.ProgressChanged -= SpeechInput_ProgressChanged;
        _speechOverlayHideTimer?.Stop();
        _speechOverlayHideTimer = null;
        try { _speechOverlay.Close(); } catch { }
    }
}

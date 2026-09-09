using System.Windows;
using System.Windows.Interop;
using HelpSys.Services;

namespace HelpSys;

public partial class ListeningOverlayWindow : Window
{
    public ListeningOverlayWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => OverlayWindow.MakeClickThrough(new WindowInteropHelper(this).Handle);
    }

    public void ShowListening(double level)
    {
        StatusText.Text = "聞き取り中…";
        TranscriptText.Text = "話し終わると自動で文字起こしします";
        LevelBar.IsIndeterminate = false;
        LevelBar.Value = Math.Clamp(level, 0d, 1d);
        EnsureShown();
    }

    public void ShowTranscribing()
    {
        StatusText.Text = "文字起こし中…";
        TranscriptText.Text = "音声を確認しています";
        LevelBar.Value = 0;
        LevelBar.IsIndeterminate = true;
        EnsureShown();
    }

    public void ShowTranscript(string text)
    {
        StatusText.Text = "聞き取り結果";
        TranscriptText.Text = string.IsNullOrWhiteSpace(text) ? "聞き取れませんでした" : text.Trim();
        LevelBar.IsIndeterminate = false;
        LevelBar.Value = 0;
        EnsureShown();
    }

    private void EnsureShown()
    {
        if (!IsVisible) Show();
        MonitorPlacementService.MoveWindowToCursorMonitorTopCenter(this, 28);
    }
}

namespace HelpSys.Models;

public sealed record GuidanceEvidenceSnapshot(
    bool ScreenshotAvailable,
    int UiElementCount,
    int InteractableCount,
    int FocusedCount,
    IReadOnlyList<string> FocusedElements,
    string ForegroundProcess,
    string ForegroundTitle,
    bool TaskbarVisible,
    int RunningAppCount,
    string? BrowserDomain,
    string? BrowserUrl,
    bool BrowserAddressFocused,
    int HistoryCount,
    IReadOnlyList<string> RecentTargets,
    IReadOnlyList<string> EvidenceSources)
{
    public int SourceCount => EvidenceSources.Count;
}

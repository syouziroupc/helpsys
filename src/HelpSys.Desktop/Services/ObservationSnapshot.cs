using HelpSys.Models;

namespace HelpSys.Services;

public sealed record ObservationSnapshot(
    long Sequence,
    DateTime CapturedUtc,
    SystemContextSnapshot System,
    IReadOnlyList<UiElementCandidate> Elements,
    string Fingerprint)
{
    public int ForegroundProcessId => System.ForegroundProcessId;
    public nint ForegroundWindowHandle => System.ForegroundWindowHandle;
}

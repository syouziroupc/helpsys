using HelpSys.Models;
using HelpSys.Services;

namespace HelpSys;

public partial class MainWindow
{
    private async Task<IReadOnlyList<UiElementCandidate>> CaptureForegroundCandidatesAsync(
        SystemContextSnapshot context,
        int maxCandidates,
        CancellationToken cancellationToken,
        bool preferWindowScope = false)
    {
        IReadOnlyList<UiElementCandidate> processCandidates = [];
        if (context.ForegroundProcessId > 0)
        {
            try
            {
                processCandidates = await _scanner.CaptureCandidatesForProcessAsync(
                    context.ForegroundProcessId,
                    maxCandidates,
                    cancellationToken);
            }
            catch (OperationCanceledException) { throw; }
            catch { processCandidates = []; }
        }

        var shouldTryWindow = context.ForegroundWindowHandle != nint.Zero &&
                              (preferWindowScope || IsWeakStructuralSnapshot(processCandidates));
        if (!shouldTryWindow) return processCandidates;

        IReadOnlyList<UiElementCandidate> windowCandidates;
        try
        {
            windowCandidates = await _scanner.CaptureCandidatesForWindowAsync(
                context.ForegroundWindowHandle,
                context.ForegroundProcessId,
                maxCandidates,
                cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch { return processCandidates; }

        if (windowCandidates.Count == 0) return processCandidates;
        if (preferWindowScope && windowCandidates.Any(x => x.Interactable && x.Enabled && !x.Bounds.IsEmpty))
            return windowCandidates;

        return StructuralSnapshotStrength(windowCandidates) > StructuralSnapshotStrength(processCandidates)
            ? windowCandidates
            : processCandidates;
    }

    private static bool IsWeakStructuralSnapshot(IReadOnlyList<UiElementCandidate> candidates)
    {
        if (candidates.Count < 8) return true;
        return candidates.Count(x => x.Interactable && x.Enabled && !x.Bounds.IsEmpty) < 3;
    }

    private static int StructuralSnapshotStrength(IReadOnlyList<UiElementCandidate> candidates)
    {
        var interactable = candidates.Count(x => x.Interactable && x.Enabled && !x.Bounds.IsEmpty);
        var namedInteractable = candidates.Count(x => x.Interactable && x.Enabled && !x.Bounds.IsEmpty && !string.IsNullOrWhiteSpace(x.Name));
        var context = candidates.Count(x => !x.Interactable && !x.Bounds.IsEmpty && !string.IsNullOrWhiteSpace(x.Name));
        return interactable * 8 + namedInteractable * 3 + Math.Min(context, 30);
    }
}

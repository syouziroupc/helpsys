from pathlib import Path


def replace_once(path: str, old: str, new: str) -> None:
    p = Path(path)
    text = p.read_text(encoding='utf-8')
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f'{path}: expected one match, got {count}: {old[:100]!r}')
    p.write_text(text.replace(old, new, 1), encoding='utf-8')

scanner = 'src/HelpSys.Desktop/Services/UiAutomationScanner.cs'
replace_once(
    scanner,
    """    public Task<UiElementCandidate?> RevalidateCandidateAsync(UiElementCandidate candidate, CancellationToken cancellationToken = default)\n        => Task.Run(() => RevalidateCandidate(candidate, cancellationToken), cancellationToken);\n""",
    """    public Task<UiElementCandidate?> RevalidateCandidateAsync(UiElementCandidate candidate, CancellationToken cancellationToken = default)\n        => Task.Run(() => RevalidateCandidate(candidate, null, cancellationToken), cancellationToken);\n\n    public Task<UiElementCandidate?> RevalidateCandidateAsync(UiElementCandidate candidate, int rootProcessId, CancellationToken cancellationToken = default)\n        => Task.Run(() => RevalidateCandidate(candidate, rootProcessId > 0 ? rootProcessId : null, cancellationToken), cancellationToken);\n""",
)
replace_once(
    scanner,
    """    private UiElementCandidate? RevalidateCandidate(UiElementCandidate candidate, CancellationToken cancellationToken)\n    {\n        var current = CaptureCandidates(700, cancellationToken, candidate.ProcessId > 0 ? candidate.ProcessId : null).Where(x => x.Interactable).ToArray();\n""",
    """    private UiElementCandidate? RevalidateCandidate(UiElementCandidate candidate, int? rootProcessId, CancellationToken cancellationToken)\n    {\n        var current = CaptureCandidates(700, cancellationToken, rootProcessId).Where(x => x.Interactable).ToArray();\n""",
)
replace_once(
    scanner,
    """        var candidates = CaptureCandidates(700, cancellationToken, visibleProcessId)\n            .Where(x => x.Interactable && !x.Bounds.IsEmpty && x.ProcessId == visibleProcessId)\n""",
    """        var candidates = CaptureCandidates(700, cancellationToken)\n            .Where(x => x.Interactable && !x.Bounds.IsEmpty && x.ProcessId == visibleProcessId)\n""",
)

main = 'src/HelpSys.Desktop/MainWindow.xaml.cs'
replace_once(
    main,
    'var candidates = await _scanner.CaptureCandidatesAsync(420, cancellationToken);',
    'var candidates = await _scanner.CaptureCandidatesForProcessAsync(systemContext.ForegroundProcessId, 420, cancellationToken);',
)
replace_once(
    main,
    'var freshTarget = await _scanner.RevalidateCandidateAsync(target, cancellationToken);',
    'var freshTarget = await _scanner.RevalidateCandidateAsync(target, systemContext.ForegroundProcessId, cancellationToken);',
)

cloud = 'src/HelpSys.Desktop/Services/CloudGuideService.cs'
replace_once(
    cloud,
    """                (foregroundId > 0\n                    ? x.ProcessId == foregroundId\n                    : !string.IsNullOrWhiteSpace(foregroundName) && x.ProcessName.Equals(foregroundName, StringComparison.OrdinalIgnoreCase)) ||\n                ShellProcesses.Contains(x.ProcessName))\n""",
    """                (foregroundId > 0 && x.ProcessId == foregroundId) ||\n                (!string.IsNullOrWhiteSpace(foregroundName) && x.ProcessName.Equals(foregroundName, StringComparison.OrdinalIgnoreCase)) ||\n                ShellProcesses.Contains(x.ProcessName))\n""",
)

print('Reliability v4 subtree follow-up applied.')

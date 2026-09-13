from pathlib import Path


def replace_once(text: str, old: str, new: str, marker: str, label: str) -> str:
    if marker in text:
        return text
    if old not in text:
        raise SystemExit(f'{label} anchor not found')
    return text.replace(old, new, 1)


path = Path('src/HelpSys.Desktop/MainWindow.LocalChoiceResolver.cs')
text = path.read_text(encoding='utf-8')

old = '''        var exact = interactable
            .Where(x => NormalizeLocalChoiceText(x.Name).Equals(normalizedAnswer, StringComparison.Ordinal))
            .ToArray();
        if (exact.Length == 1) return exact[0];
        if (exact.Length > 1) return null;

        var partial = interactable
'''
new = '''        var exact = interactable
            .Where(x => NormalizeLocalChoiceText(x.Name).Equals(normalizedAnswer, StringComparison.Ordinal))
            .ToArray();
        if (exact.Length == 1) return exact[0];
        if (exact.Length > 1)
        {
            var contextualExact = FindUniqueContainingLocalChoice(candidates, interactable, normalizedAnswer, exactOnly: true);
            if (contextualExact is not null) return contextualExact;
        }

        var contextual = FindUniqueContainingLocalChoice(candidates, interactable, normalizedAnswer, exactOnly: false);
        if (contextual is not null) return contextual;

        var partial = interactable
'''
text = replace_once(text, old, new, 'FindUniqueContainingLocalChoice(candidates, interactable, normalizedAnswer', 'context mapping hook')

anchor = '''    private static string NormalizeLocalChoiceText(string value)
'''
helper = '''    private static UiElementCandidate? FindUniqueContainingLocalChoice(
        IReadOnlyList<UiElementCandidate> candidates,
        IReadOnlyList<UiElementCandidate> interactable,
        string normalizedAnswer,
        bool exactOnly)
    {
        // Some browser account choosers expose the visible identity detail (for example an email
        // address) as a non-interactive Text child while the clickable account card has only a
        // duplicated display name. Keep the identity detail local and map it only to an interactable
        // element that geometrically contains the matching Text/context node. No nearest-neighbour
        // guessing is allowed: ambiguity fails closed and asks the user again.
        if (normalizedAnswer.Length < 3) return null;

        var contextMatches = candidates
            .Where(x => !x.Interactable && x.Enabled && !x.Bounds.IsEmpty && !string.IsNullOrWhiteSpace(x.Name))
            .Where(x =>
            {
                var name = NormalizeLocalChoiceText(x.Name);
                if (name.Length == 0) return false;
                return exactOnly
                    ? name.Equals(normalizedAnswer, StringComparison.Ordinal)
                    : name.Equals(normalizedAnswer, StringComparison.Ordinal) ||
                      name.Contains(normalizedAnswer, StringComparison.Ordinal) ||
                      normalizedAnswer.Contains(name, StringComparison.Ordinal);
            })
            .ToArray();
        if (contextMatches.Length == 0) return null;

        var mapped = new Dictionary<string, UiElementCandidate>(StringComparer.Ordinal);
        foreach (var context in contextMatches)
        {
            var center = new System.Windows.Point(
                context.X + context.Width / 2d,
                context.Y + context.Height / 2d);

            var containers = interactable
                .Where(x => !LocalChoiceUtilityRegex.IsMatch(x.Name.Trim()))
                .Where(x => context.ProcessId <= 0 || x.ProcessId <= 0 || x.ProcessId == context.ProcessId)
                .Where(x => x.Bounds.Contains(center))
                .OrderBy(x => x.Width * x.Height)
                .ToArray();

            // Prefer the smallest containing clickable card. If two candidates have effectively the
            // same area, the UIA structure is ambiguous and we do not guess.
            if (containers.Length == 0) continue;
            var smallestArea = containers[0].Width * containers[0].Height;
            var smallest = containers
                .Where(x => Math.Abs((x.Width * x.Height) - smallestArea) <= 1d)
                .ToArray();
            if (smallest.Length != 1) continue;
            mapped[smallest[0].Id] = smallest[0];
        }

        return mapped.Count == 1 ? mapped.Values.Single() : null;
    }

''' + anchor
text = replace_once(text, anchor, helper, 'private static UiElementCandidate? FindUniqueContainingLocalChoice(', 'context mapping helper')
path.write_text(text, encoding='utf-8')


contract_path = Path('tests/local-choice-contract.mjs')
contract = contract_path.read_text(encoding='utf-8')
addition = r'''
if (!local.includes('FindUniqueContainingLocalChoice(candidates, interactable, normalizedAnswer'))
  throw new Error('local resolver must support context-text to containing account-card mapping');
if (!local.includes('x.Bounds.Contains(center)'))
  throw new Error('context identity mapping must require geometric containment, not nearest-neighbour guessing');
if (!local.includes('mapped.Count == 1 ? mapped.Values.Single() : null'))
  throw new Error('context identity mapping must fail closed unless exactly one clickable target remains');
if (!local.includes('!x.Interactable && x.Enabled') || !local.includes('context.ProcessId <= 0 || x.ProcessId <= 0 || x.ProcessId == context.ProcessId'))
  throw new Error('context identity mapping must stay local to visible context and the same UIA process when known');
'''
marker = 'context-text to containing account-card mapping'
if marker not in contract:
    console = "console.log('HelpSys local account-choice privacy contract passed.');"
    if console not in contract:
        raise SystemExit('local-choice contract console anchor not found')
    contract = contract.replace(console, addition + '\n' + console, 1)
    contract_path.write_text(contract, encoding='utf-8')

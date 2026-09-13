from pathlib import Path
import json
import re

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding='utf-8')


def write(path, text):
    (ROOT / path).write_text(text, encoding='utf-8', newline='\n')


def replace_once(text, old, new, label):
    count = text.count(old)
    if count == 0 and new in text:
        return text
    if count != 1:
        raise RuntimeError(f'{label}: expected exactly one anchor, found {count}')
    return text.replace(old, new, 1)


# A) Make visible-choice detection genuinely generic and add explicit positional/ordinal answers.
path = 'src/HelpSys.Desktop/MainWindow.LocalChoiceResolver.cs'
text = read(path)
old_regex = r'''    private static readonly Regex LocalVisibleChoiceQuestionRegex = new(
        @"(?:どれ|どちら|いずれ|どの(?:項目|ボタン|選択肢|アカウント|プロフィール|プロファイル|ユーザー|ファイル|フォルダー|メニュー|設定|方法)).*(?:使|選|押|開)|(?:選んで|選択して|選びますか|選びたい)|\\b(?:which|choose|select|pick)\\b(?:.{0,80})\\b(?:option|item|account|profile|button|file|folder|menu|one)\\b|^(?:choose|select|pick)\\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex LocalSensitiveChoiceQuestionRegex = new(
        @"アカウント|プロフィール|プロファイル|ユーザー|account|profile|user",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
'''
new_regex = r'''    private static readonly Regex LocalVisibleChoiceQuestionRegex = new(
        @"(?:どれ|どちら|いずれ|どの(?:項目|ボタン|選択肢|アカウント|プロフィール|プロファイル|ユーザー|ファイル|フォルダー|メニュー|設定|方法)).{0,80}(?:使|選|押|開)|(?:項目|選択肢|アカウント|プロフィール|プロファイル|ユーザー|ファイル|フォルダー|メニュー|設定).{0,36}(?:選択|選ぶ|選んで)|(?:選んで|選択して|選択してください|選びますか|選びたい)|\b(?:which|choose|select|pick)\b(?:.{0,80})\b(?:option|item|account|profile|button|file|folder|menu|one)\b|^(?:choose|select|pick)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex LocalChoiceOrdinalRegex = new(
        @"(?:上から|下から)?(?<number>[1-9])番目|^(?<word>first|second|third|fourth|fifth|last|top|bottom|left|right|一番上|一番下|上|下|左|右)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
'''
text = replace_once(text, old_regex, new_regex, 'visible choice regexes')
text = text.replace(
    'return LocalVisibleChoiceQuestionRegex.IsMatch(question) || LocalSensitiveChoiceQuestionRegex.IsMatch(question);',
    'return LocalVisibleChoiceQuestionRegex.IsMatch(question);')

needle = '''        var contextual = FindUniqueContainingLocalChoice(candidates, interactable, normalizedAnswer, exactOnly: false);
        if (contextual is not null) return contextual;

        if (normalizedAnswer.Length < 3) return null;
'''
replacement = '''        var contextual = FindUniqueContainingLocalChoice(candidates, interactable, normalizedAnswer, exactOnly: false);
        if (contextual is not null) return contextual;

        var positional = FindPositionalLocalVisibleChoice(interactable, answer);
        if (positional is not null) return positional;

        if (normalizedAnswer.Length < 3) return null;
'''
text = replace_once(text, needle, replacement, 'positional choice hook')

insert_anchor = '''    private static UiElementCandidate? FindUniqueContainingLocalChoice(
'''
if 'FindPositionalLocalVisibleChoice' not in text.split(insert_anchor)[0]:
    raise RuntimeError('positional hook was not inserted before containment method')
if 'private static UiElementCandidate? FindPositionalLocalVisibleChoice' not in text:
    positional_method = r'''    private static UiElementCandidate? FindPositionalLocalVisibleChoice(
        IReadOnlyList<UiElementCandidate> interactable,
        string answer)
    {
        if (string.IsNullOrWhiteSpace(answer)) return null;
        var pool = CollapsePositionalChoiceCandidates(interactable
            .Where(x => string.IsNullOrWhiteSpace(x.Name) || !LocalChoiceUtilityRegex.IsMatch(x.Name.Trim()))
            .ToArray());
        if (pool.Count < 2) return null;

        var normalized = answer.Normalize(NormalizationForm.FormKC).Trim().ToLowerInvariant();
        var match = LocalChoiceOrdinalRegex.Match(normalized);
        if (!match.Success) return null;

        if (match.Groups["number"].Success && int.TryParse(match.Groups["number"].Value, out var ordinal))
        {
            var reverse = normalized.StartsWith("下から", StringComparison.Ordinal);
            var ordered = OrderVisualChoices(pool, reverse).ToArray();
            return ordinal >= 1 && ordinal <= ordered.Length ? ordered[ordinal - 1] : null;
        }

        return match.Groups["word"].Value switch
        {
            "first" => OrderVisualChoices(pool, false).FirstOrDefault(),
            "second" => OrderVisualChoices(pool, false).Skip(1).FirstOrDefault(),
            "third" => OrderVisualChoices(pool, false).Skip(2).FirstOrDefault(),
            "fourth" => OrderVisualChoices(pool, false).Skip(3).FirstOrDefault(),
            "fifth" => OrderVisualChoices(pool, false).Skip(4).FirstOrDefault(),
            "last" => OrderVisualChoices(pool, false).LastOrDefault(),
            "top" or "一番上" or "上" => FindUniqueDirectionalExtreme(pool, vertical: true, minimum: true),
            "bottom" or "一番下" or "下" => FindUniqueDirectionalExtreme(pool, vertical: true, minimum: false),
            "left" or "左" => FindUniqueDirectionalExtreme(pool, vertical: false, minimum: true),
            "right" or "右" => FindUniqueDirectionalExtreme(pool, vertical: false, minimum: false),
            _ => null
        };
    }

    private static IReadOnlyList<UiElementCandidate> CollapsePositionalChoiceCandidates(
        IReadOnlyList<UiElementCandidate> candidates)
    {
        var kept = new List<UiElementCandidate>();
        foreach (var candidate in candidates.OrderBy(x => x.Width * x.Height))
        {
            var cx = candidate.X + candidate.Width / 2d;
            var cy = candidate.Y + candidate.Height / 2d;
            if (kept.Any(existing =>
            {
                var ex = existing.X + existing.Width / 2d;
                var ey = existing.Y + existing.Height / 2d;
                return Math.Abs(cx - ex) <= 8 && Math.Abs(cy - ey) <= 8;
            })) continue;
            kept.Add(candidate);
        }
        return kept;
    }

    private static IEnumerable<UiElementCandidate> OrderVisualChoices(
        IReadOnlyList<UiElementCandidate> candidates,
        bool reverse)
    {
        var ordered = candidates
            .OrderBy(x => x.Y + x.Height / 2d)
            .ThenBy(x => x.X + x.Width / 2d);
        return reverse ? ordered.Reverse() : ordered;
    }

    private static UiElementCandidate? FindUniqueDirectionalExtreme(
        IReadOnlyList<UiElementCandidate> candidates,
        bool vertical,
        bool minimum)
    {
        var values = candidates.Select(x => vertical ? x.Y + x.Height / 2d : x.X + x.Width / 2d).ToArray();
        var extreme = minimum ? values.Min() : values.Max();
        var tolerance = 18d;
        var matches = candidates
            .Where(x => Math.Abs((vertical ? x.Y + x.Height / 2d : x.X + x.Width / 2d) - extreme) <= tolerance)
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

'''
    text = replace_once(text, insert_anchor, positional_method + insert_anchor, 'positional methods')

# Use the foreground-aware revalidator for locally resolved choices.
text = text.replace(
    'fresh = await _scanner.RevalidateCandidateAsync(match, context.ForegroundProcessId, token);',
    'fresh = await RevalidateForegroundCandidateAsync(match, context, token);')
write(path, text)

# B) Add window-scoped target revalidation with stronger duplicate/ambiguity protection.
path = 'src/HelpSys.Desktop/Services/UiAutomationScanner.cs'
text = read(path)
if 'RevalidateCandidateForWindowAsync' not in text:
    anchor = '''    public Task<Rect?> SnapToAccessibleBoundsAsync(Rect approximateBounds, CancellationToken cancellationToken = default)
'''
    public_method = '''    public Task<UiElementCandidate?> RevalidateCandidateForWindowAsync(
        UiElementCandidate candidate,
        nint windowHandle,
        int expectedProcessId,
        CancellationToken cancellationToken = default)
    {
        if (windowHandle == nint.Zero) return Task.FromResult<UiElementCandidate?>(null);
        return Task.Run(
            () => RevalidateCandidateForWindow(candidate, windowHandle, expectedProcessId, cancellationToken),
            cancellationToken);
    }

'''
    text = replace_once(text, anchor, public_method + anchor, 'window revalidate public method')

start = text.index('    private UiElementCandidate? RevalidateCandidate(UiElementCandidate candidate, int? rootProcessId, CancellationToken cancellationToken)')
end = text.index('    private Rect? SnapToAccessibleBounds', start)
old = text[start:end]
new = r'''    private UiElementCandidate? RevalidateCandidate(UiElementCandidate candidate, int? rootProcessId, CancellationToken cancellationToken)
    {
        var current = CaptureCandidates(700, cancellationToken, rootProcessId)
            .Where(x => x.Interactable && x.Enabled && !x.Bounds.IsEmpty)
            .ToArray();
        return FindRevalidatedCandidate(candidate, current, strictProcessIdentity: true, cancellationToken);
    }

    private UiElementCandidate? RevalidateCandidateForWindow(
        UiElementCandidate candidate,
        nint windowHandle,
        int expectedProcessId,
        CancellationToken cancellationToken)
    {
        var current = CaptureCandidatesForWindow(windowHandle, expectedProcessId, 700, cancellationToken)
            .Where(x => x.Interactable && x.Enabled && !x.Bounds.IsEmpty)
            .ToArray();
        return FindRevalidatedCandidate(candidate, current, strictProcessIdentity: false, cancellationToken);
    }

    private static UiElementCandidate? FindRevalidatedCandidate(
        UiElementCandidate candidate,
        IReadOnlyList<UiElementCandidate> current,
        bool strictProcessIdentity,
        CancellationToken cancellationToken)
    {
        if (current.Count == 0) return null;
        var oldCenterX = candidate.X + candidate.Width / 2d;
        var oldCenterY = candidate.Y + candidate.Height / 2d;
        var oldRect = candidate.Bounds;

        bool SameProcess(UiElementCandidate x) => candidate.ProcessId > 0
            ? x.ProcessId == candidate.ProcessId
            : !string.IsNullOrWhiteSpace(candidate.ProcessName) &&
              x.ProcessName.Equals(candidate.ProcessName, StringComparison.OrdinalIgnoreCase);

        var comparable = current
            .Where(x => x.ControlType.Equals(candidate.ControlType, StringComparison.OrdinalIgnoreCase))
            .Where(x => !strictProcessIdentity || SameProcess(x))
            .ToArray();
        if (comparable.Length == 0) return null;

        var automationIdMultiplicity = string.IsNullOrWhiteSpace(candidate.AutomationId)
            ? 0
            : comparable.Count(x => x.AutomationId.Equals(candidate.AutomationId, StringComparison.Ordinal));
        var nameMultiplicity = string.IsNullOrWhiteSpace(candidate.Name)
            ? 0
            : comparable.Count(x => x.Name.Equals(candidate.Name, StringComparison.OrdinalIgnoreCase));

        var ranked = new List<(UiElementCandidate Item, double Score, bool StrongUniqueIdentity)>();
        foreach (var item in comparable)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sameProcessId = candidate.ProcessId > 0 && item.ProcessId == candidate.ProcessId;
            var sameProcessName = !string.IsNullOrWhiteSpace(candidate.ProcessName) &&
                                  item.ProcessName.Equals(candidate.ProcessName, StringComparison.OrdinalIgnoreCase);
            var exactAutomationId = !string.IsNullOrWhiteSpace(candidate.AutomationId) &&
                                    item.AutomationId.Equals(candidate.AutomationId, StringComparison.Ordinal);
            var exactName = !string.IsNullOrWhiteSpace(candidate.Name) &&
                            item.Name.Equals(candidate.Name, StringComparison.OrdinalIgnoreCase);
            var exactClass = !string.IsNullOrWhiteSpace(candidate.ClassName) &&
                             item.ClassName.Equals(candidate.ClassName, StringComparison.Ordinal);

            var centerX = item.X + item.Width / 2d;
            var centerY = item.Y + item.Height / 2d;
            var distance = Math.Sqrt(Math.Pow(centerX - oldCenterX, 2) + Math.Pow(centerY - oldCenterY, 2));
            var overlap = oldRect.IntersectsWith(item.Bounds) ? Rect.Intersect(oldRect, item.Bounds) : Rect.Empty;
            var overlapArea = overlap.IsEmpty ? 0d : overlap.Width * overlap.Height;
            var smallerArea = Math.Max(1d, Math.Min(oldRect.Width * oldRect.Height, item.Width * item.Height));
            var overlapRatio = overlapArea / smallerArea;
            var positionalIdentity = exactClass && distance <= 65 && overlapRatio >= 0.35;
            var uniqueAutomationId = exactAutomationId && automationIdMultiplicity == 1;
            var uniqueName = exactName && nameMultiplicity == 1;

            if (!uniqueAutomationId && !uniqueName && !positionalIdentity) continue;

            // Window-scoped fallback may survive renderer/child PID churn, but only with both a
            // unique semantic identity and nearby/overlapping geometry inside the verified HWND.
            if (!strictProcessIdentity && !sameProcessId && !sameProcessName)
            {
                var strongCrossProcessIdentity = (uniqueAutomationId || uniqueName) &&
                                                 (overlapRatio >= 0.20 || distance <= 45);
                if (!strongCrossProcessIdentity) continue;
            }

            var score = 45d;
            if (sameProcessId) score += 35;
            else if (sameProcessName) score += 15;
            if (uniqueAutomationId) score += 190;
            else if (exactAutomationId) score += 85;
            if (uniqueName) score += 145;
            else if (exactName) score += 70;
            if (exactClass) score += 35;
            if (positionalIdentity) score += 45;
            score += Math.Min(90, overlapRatio * 90);
            score -= Math.Min(120, distance / 7d);

            ranked.Add((item, score, uniqueAutomationId || uniqueName));
        }

        if (ranked.Count == 0) return null;
        var ordered = ranked.OrderByDescending(x => x.Score).ToArray();
        var best = ordered[0];
        if (best.Score < 125) return null;

        // If two non-unique identities score almost the same, the UI moved into an ambiguous state.
        // Fail closed and let current-state replan/clarification reacquire the screen instead of
        // silently switching the blue frame to a sibling control with the same label.
        if (ordered.Length > 1 && !best.StrongUniqueIdentity && best.Score - ordered[1].Score < 25)
            return null;

        return best.Item;
    }

'''
text = text[:start] + new + text[end:]
write(path, text)

# C) Central foreground-aware revalidation helper and use it in planning/verification/live watcher.
path = 'src/HelpSys.Desktop/MainWindow.StructuralCapture.cs'
text = read(path)
if 'RevalidateForegroundCandidateAsync' not in text:
    anchor = '\n    private static bool IsWeakStructuralSnapshot'
    method = r'''
    private async Task<UiElementCandidate?> RevalidateForegroundCandidateAsync(
        UiElementCandidate candidate,
        SystemContextSnapshot expectedContext,
        CancellationToken cancellationToken)
    {
        var before = _systemContext.Capture();
        if (!HasSameCaptureIdentity(expectedContext, before) || HasSystemTransitionV3(expectedContext, before))
            return null;

        UiElementCandidate? strict = null;
        if (expectedContext.ForegroundProcessId > 0)
        {
            try
            {
                strict = await _scanner.RevalidateCandidateAsync(
                    candidate,
                    expectedContext.ForegroundProcessId,
                    cancellationToken);
            }
            catch (OperationCanceledException) { throw; }
            catch { strict = null; }
        }
        if (strict is not null) return strict;

        var afterStrict = _systemContext.Capture();
        if (expectedContext.ForegroundWindowHandle == nint.Zero ||
            !HasSameCaptureIdentity(expectedContext, afterStrict) ||
            HasSystemTransitionV3(expectedContext, afterStrict))
            return null;

        try
        {
            return await _scanner.RevalidateCandidateForWindowAsync(
                candidate,
                expectedContext.ForegroundWindowHandle,
                expectedContext.ForegroundProcessId,
                cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }
'''
    text = text.replace(anchor, method + anchor, 1)
write(path, text)

path = 'src/HelpSys.Desktop/MainWindow.QualityFirst.cs'
text = read(path).replace(
    'await _scanner.RevalidateCandidateAsync(target, systemContext.ForegroundProcessId, cancellationToken)',
    'await RevalidateForegroundCandidateAsync(target, systemContext, cancellationToken)')
text = text.replace(
    'await _scanner.RevalidateCandidateAsync(target, expectedContext.ForegroundProcessId, cancellationToken)',
    'await RevalidateForegroundCandidateAsync(target, expectedContext, cancellationToken)')
write(path, text)

path = 'src/HelpSys.Desktop/MainWindow.ReliabilityV3.cs'
text = read(path)
old_block = '''            var rootProcessId = _stepSystemBaseline?.ForegroundProcessId ?? 0;
            var fresh = rootProcessId > 0
                ? await _scanner.RevalidateCandidateAsync(_currentTarget, rootProcessId, cancellationToken)
                : await _scanner.RevalidateCandidateAsync(_currentTarget, cancellationToken);
'''
new_block = '''            var expectedContext = _stepSystemBaseline;
            var fresh = expectedContext is not null
                ? await RevalidateForegroundCandidateAsync(_currentTarget, expectedContext, cancellationToken)
                : await _scanner.RevalidateCandidateAsync(_currentTarget, cancellationToken);
'''
text = replace_once(text, old_block, new_block, 'Reliability target revalidation')
write(path, text)

path = 'src/HelpSys.Desktop/MainWindow.StableGuidance.cs'
text = read(path).replace(
    'nowElements = await _scanner.CaptureCandidatesForProcessAsync(nowSystem.ForegroundProcessId, 320, token);',
    'nowElements = await CaptureForegroundCandidatesAsync(nowSystem, 320, token);')
write(path, text)

# D) Focused contracts.
path = 'tests/local-choice-contract.mjs'
test = read(path)
extra = r'''
if (local.includes('LocalSensitiveChoiceQuestionRegex'))
  throw new Error('generic visible-choice interception must not trigger merely because a clarification mentions user/account/profile');
if (!local.includes('\\b(?:which|choose|select|pick)\\b') || local.includes('\\\\b(?:which|choose|select|pick)'))
  throw new Error('C# verbatim Regex must use a single backslash for English word boundaries');
if (!local.includes('FindPositionalLocalVisibleChoice') || !local.includes('LocalChoiceOrdinalRegex'))
  throw new Error('generic visible-choice resolution must support explicit ordinal/positional answers');
if (!local.includes('CollapsePositionalChoiceCandidates') || !local.includes('FindUniqueDirectionalExtreme'))
  throw new Error('positional choice resolution must collapse duplicate centers and fail closed on tied directional extremes');
if (!local.includes('LocalChoiceUtilityRegex.IsMatch(x.Name.Trim())'))
  throw new Error('ordinal choice resolution must not silently count utility controls such as Cancel/Back');
if (!local.includes('RevalidateForegroundCandidateAsync(match, context, token)'))
  throw new Error('locally selected choices must use foreground-aware target revalidation');
'''
if 'generic visible-choice interception must not trigger' not in test:
    test = test.replace("console.log('HelpSys generic visible-choice privacy contract passed.');", extra + "\nconsole.log('HelpSys generic visible-choice privacy contract passed.');")
write(path, test)

revalidation_test = r'''import fs from 'node:fs';

const scanner = fs.readFileSync('src/HelpSys.Desktop/Services/UiAutomationScanner.cs', 'utf8');
const capture = fs.readFileSync('src/HelpSys.Desktop/MainWindow.StructuralCapture.cs', 'utf8');
const quality = fs.readFileSync('src/HelpSys.Desktop/MainWindow.QualityFirst.cs', 'utf8');
const reliability = fs.readFileSync('src/HelpSys.Desktop/MainWindow.ReliabilityV3.cs', 'utf8');
const stable = fs.readFileSync('src/HelpSys.Desktop/MainWindow.StableGuidance.cs', 'utf8');

if (!scanner.includes('RevalidateCandidateForWindowAsync') || !scanner.includes('FindRevalidatedCandidate'))
  throw new Error('scanner must support HWND-scoped revalidation through the shared identity matcher');
if (!scanner.includes('strictProcessIdentity: false'))
  throw new Error('HWND revalidation must explicitly allow child/renderer PID churn only inside verified window scope');
if (!scanner.includes('strongCrossProcessIdentity') || !scanner.includes('overlapRatio >= 0.20 || distance <= 45'))
  throw new Error('cross-process HWND revalidation must require unique semantic identity plus nearby geometry');
if (!scanner.includes('nameMultiplicity') || !scanner.includes('best.Score - ordered[1].Score < 25'))
  throw new Error('revalidation must fail closed for near-tied duplicate labels rather than choosing a sibling');
if (!capture.includes('RevalidateForegroundCandidateAsync') || !capture.includes('HasSameCaptureIdentity(expectedContext, before)'))
  throw new Error('foreground revalidation must reject stale HWND/context before trying fallback');
if (!capture.includes('RevalidateCandidateAsync') || !capture.includes('RevalidateCandidateForWindowAsync'))
  throw new Error('foreground revalidation must try strict PID identity first and verified HWND fallback second');
const qualityUses = quality.match(/RevalidateForegroundCandidateAsync\(/g)?.length ?? 0;
if (qualityUses < 2)
  throw new Error('quality planning and structured fallback must both use foreground-aware revalidation');
if (!reliability.includes('RevalidateForegroundCandidateAsync(_currentTarget, expectedContext'))
  throw new Error('post-action target verification must use foreground-aware revalidation');
if (!stable.includes('CaptureForegroundCandidatesAsync(nowSystem, 320, token)'))
  throw new Error('live semantic watcher must use the same resilient foreground structural capture path');

console.log('HelpSys window-scoped target revalidation contract passed.');
'''
write('tests/window-scoped-revalidation-contract.mjs', revalidation_test)

path = 'package.json'
pkg = json.loads(read(path))
for key, command in [
    ('check', 'node --check tests/window-scoped-revalidation-contract.mjs'),
    ('test', 'node tests/window-scoped-revalidation-contract.mjs')
]:
    if command not in pkg['scripts'][key]:
        anchor = 'node --check tests/window-scoped-uia-contract.mjs' if key == 'check' else 'node tests/window-scoped-uia-contract.mjs'
        pkg['scripts'][key] = pkg['scripts'][key].replace(anchor, anchor + ' && ' + command)
write(path, json.dumps(pkg, ensure_ascii=False, indent=2) + '\n')

print('Applied generic UI resolution follow-up hardening.')

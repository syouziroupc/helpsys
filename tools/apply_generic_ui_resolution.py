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
    if count != 1:
        raise RuntimeError(f'{label}: expected exactly one anchor, found {count}')
    return text.replace(old, new, 1)


# 1) Generalize account-only local choice handling into a visible-choice resolver.
local = r'''using System.Text;
using System.Text.RegularExpressions;
using HelpSys.Models;
using HelpSys.Services;

namespace HelpSys;

public partial class MainWindow
{
    private static readonly Regex LocalVisibleChoiceQuestionRegex = new(
        @"(?:どれ|どちら|いずれ|どの(?:項目|ボタン|選択肢|アカウント|プロフィール|プロファイル|ユーザー|ファイル|フォルダー|メニュー|設定|方法)).*(?:使|選|押|開)|(?:選んで|選択して|選びますか|選びたい)|\\b(?:which|choose|select|pick)\\b(?:.{0,80})\\b(?:option|item|account|profile|button|file|folder|menu|one)\\b|^(?:choose|select|pick)\\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex LocalSensitiveChoiceQuestionRegex = new(
        @"アカウント|プロフィール|プロファイル|ユーザー|account|profile|user",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex LocalChoiceUtilityRegex = new(
        @"^(?:戻る|back|キャンセル|cancel|閉じる|close|その他|more|設定|settings|ヘルプ|help)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly HashSet<string> LocalChoiceControlTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Button", "ListItem", "MenuItem", "Hyperlink", "TabItem", "ComboBox",
        "CheckBox", "RadioButton", "TreeItem"
    };

    private bool _localChoiceTargetActive;

    private enum LocalChoiceAnswerResult
    {
        NotApplicable,
        Handled
    }

    private async Task<LocalChoiceAnswerResult> TryHandleLocalVisibleChoiceAnswerAsync(string answer)
    {
        var question = _clarificationQuestion;
        if (!LooksLikeVisibleChoiceQuestion(question))
            return LocalChoiceAnswerResult.NotApplicable;

        if (_sessionCts is null || _sessionCts.IsCancellationRequested || _activeRequest is null)
            return LocalChoiceAnswerResult.Handled;

        var token = _sessionCts.Token;
        var context = _systemContext.Capture();
        if (!HasUsableForeground(context))
        {
            KeepLocalVisibleChoiceClarification(BuildLocalChoiceRetryQuestion(question,
                "現在の選択画面を端末内で確認できませんでした。画面を表示したまま、選びたい項目の表示名をもう一度入力してください。"));
            return LocalChoiceAnswerResult.Handled;
        }

        IReadOnlyList<UiElementCandidate> candidates;
        try
        {
            candidates = await CaptureForegroundCandidatesAsync(context, 520, token, preferWindowScope: true);
        }
        catch (OperationCanceledException) { return LocalChoiceAnswerResult.Handled; }
        catch
        {
            KeepLocalVisibleChoiceClarification(BuildLocalChoiceRetryQuestion(question,
                "現在の選択肢を端末内で読み取れませんでした。画面を表示したまま、もう一度同じ表示名を入力してください。"));
            return LocalChoiceAnswerResult.Handled;
        }

        if (!HasLocalVisibleChoiceSurface(candidates))
        {
            KeepLocalVisibleChoiceClarification(BuildLocalChoiceRetryQuestion(question,
                "画面上の選択肢を安全に確認できませんでした。選択画面を表示したまま、項目の表示名をそのまま入力してください。"));
            return LocalChoiceAnswerResult.Handled;
        }

        var match = FindUniqueLocalVisibleChoice(candidates, answer);
        if (match is null)
        {
            KeepLocalVisibleChoiceClarification(BuildLocalChoiceRetryQuestion(question,
                "回答と画面上の項目を1つに特定できませんでした。画面に表示されている選びたい項目の名前を、そのまま1つ入力してください。"));
            return LocalChoiceAnswerResult.Handled;
        }

        UiElementCandidate? fresh;
        try
        {
            fresh = await _scanner.RevalidateCandidateAsync(match, context.ForegroundProcessId, token);
        }
        catch (OperationCanceledException) { return LocalChoiceAnswerResult.Handled; }
        catch { fresh = null; }

        if (fresh is null || HasSystemTransitionV3(context, _systemContext.Capture()))
        {
            KeepLocalVisibleChoiceClarification(BuildLocalChoiceRetryQuestion(question,
                "選んだ項目の位置または画面状態が変わりました。現在表示されている項目名をもう一度入力してください。"));
            return LocalChoiceAnswerResult.Handled;
        }

        var generation = _sessionState.Generation;
        if (!_sessionState.TryTransition(generation, GuidanceSessionState.Capturing) ||
            !_sessionState.TryTransition(generation, GuidanceSessionState.Planning))
        {
            KeepLocalVisibleChoiceClarification(BuildLocalChoiceRetryQuestion(question,
                "選択の案内状態を更新できませんでした。現在の画面に出ている項目名をもう一度入力してください。"));
            return LocalChoiceAnswerResult.Handled;
        }

        _history.Add(new GuideHistoryItem(
            _stepNumber,
            "local_choice_answer",
            "利用者が選んだ項目",
            "利用者の回答を端末内だけで現在の可視選択肢と照合し、回答内容を外部送信せず対象を確定した。"));
        if (_history.Count > 12) _history.RemoveAt(0);

        _clarificationQuestion = null;
        HideClarificationUiIfNeeded(force: true);
        RequestBox.Text = _originalRequest ?? _activeRequest;
        RequestBox.CaretIndex = RequestBox.Text.Length;
        _localChoiceTargetActive = true;
        try { _actionObserver.Start(); } catch { }

        var decision = new GuideDecision(
            "target",
            fresh.Id,
            "left_click",
            "青い枠の、あなたが選んだ項目をマウスの左ボタンで1回押してください。",
            null,
            null,
            0.99);

        ShowStructuredTarget(decision, fresh, candidates, context, generation);
        return LocalChoiceAnswerResult.Handled;
    }

    private void KeepLocalVisibleChoiceClarification(string question)
    {
        _localChoiceTargetActive = false;
        WaitForClarification(question);
        EnsureClarificationUi();
    }

    private static bool LooksLikeVisibleChoiceQuestion(string? question)
    {
        if (string.IsNullOrWhiteSpace(question)) return false;
        return LocalVisibleChoiceQuestionRegex.IsMatch(question) || LocalSensitiveChoiceQuestionRegex.IsMatch(question);
    }

    private static string BuildLocalChoiceRetryQuestion(string? originalQuestion, string fallback)
    {
        if (string.IsNullOrWhiteSpace(originalQuestion)) return fallback;
        return $"{fallback} 元の確認: {originalQuestion}";
    }

    private static bool HasLocalVisibleChoiceSurface(IReadOnlyList<UiElementCandidate> candidates)
    {
        var interactable = candidates
            .Where(IsLocalChoiceCandidate)
            .ToArray();
        if (interactable.Length < 2) return false;

        var named = interactable
            .Select(x => NormalizeLocalChoiceText(x.Name))
            .Where(x => x.Length > 0 && x != "inputfield" && x != "passwordfield")
            .Distinct(StringComparer.Ordinal)
            .Take(2)
            .Count();
        if (named >= 2) return true;

        // Some web/custom chooser rows are nameless clickable cards whose child Text nodes carry
        // the actual labels. Two interactable containers plus visible context text is sufficient to
        // attempt a containment-only match; ambiguity still fails closed below.
        return candidates.Any(x => !x.Interactable && x.Enabled && !x.Bounds.IsEmpty && !string.IsNullOrWhiteSpace(x.Name));
    }

    private static bool IsLocalChoiceCandidate(UiElementCandidate candidate)
    {
        if (!candidate.Interactable || !candidate.Enabled || candidate.Bounds.IsEmpty) return false;
        if (!LocalChoiceControlTypes.Contains(candidate.ControlType)) return false;
        if (candidate.Password) return false;
        return candidate.Width >= 8 && candidate.Height >= 8;
    }

    private static UiElementCandidate? FindUniqueLocalVisibleChoice(
        IReadOnlyList<UiElementCandidate> candidates,
        string answer)
    {
        var normalizedAnswer = NormalizeLocalChoiceText(answer);
        if (string.IsNullOrWhiteSpace(normalizedAnswer)) return null;

        var interactable = candidates
            .Where(IsLocalChoiceCandidate)
            .ToArray();

        var exact = interactable
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

        if (normalizedAnswer.Length < 3) return null;
        var partial = interactable
            .Where(x => !string.IsNullOrWhiteSpace(x.Name))
            .Where(x => !LocalChoiceUtilityRegex.IsMatch(x.Name.Trim()))
            .Where(x =>
            {
                var name = NormalizeLocalChoiceText(x.Name);
                return name.Length >= 3 &&
                       (name.Contains(normalizedAnswer, StringComparison.Ordinal) ||
                        normalizedAnswer.Contains(name, StringComparison.Ordinal));
            })
            .ToArray();

        return partial.Length == 1 ? partial[0] : null;
    }

    private static UiElementCandidate? FindUniqueContainingLocalChoice(
        IReadOnlyList<UiElementCandidate> candidates,
        IReadOnlyList<UiElementCandidate> interactable,
        string normalizedAnswer,
        bool exactOnly)
    {
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
                .Where(x => x.Bounds.Contains(center))
                .OrderBy(x => x.Width * x.Height)
                .ToArray();

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

    private static string NormalizeLocalChoiceText(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var normalized = value.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        var builder = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            if (char.IsWhiteSpace(ch)) continue;
            if (ch is '「' or '」' or '『' or '』' or '"' or '\'' or '(' or ')' or '（' or '）' or '[' or ']' or '【' or '】' or '<' or '>' or '＜' or '＞') continue;
            builder.Append(ch);
        }
        return builder.ToString();
    }
}
'''
write('src/HelpSys.Desktop/MainWindow.LocalChoiceResolver.cs', local)

# 2) Hook both clarification paths into the generic local resolver.
for path in ['src/HelpSys.Desktop/MainWindow.xaml.cs', 'src/HelpSys.Desktop/MainWindow.LiveGuidance.cs']:
    text = read(path)
    text = text.replace('TryHandleLocalAccountChoiceAnswerAsync', 'TryHandleLocalVisibleChoiceAnswerAsync')
    write(path, text)

# Keep post-action history opaque for every locally resolved visible choice.
path = 'src/HelpSys.Desktop/MainWindow.ReliabilityV3.cs'
text = read(path).replace('"利用者が選んだアカウント"', '"利用者が選んだ項目"')
text = text.replace('await _scanner.CaptureCandidatesForProcessAsync(_stepSystemBaseline.ForegroundProcessId, 420, _sessionCts.Token)',
                    'await CaptureForegroundCandidatesAsync(_stepSystemBaseline, 420, _sessionCts.Token)')
write(path, text)

# 3) Add HWND-scoped capture to the existing scanner without weakening candidate rules.
path = 'src/HelpSys.Desktop/Services/UiAutomationScanner.cs'
text = read(path)
public_anchor = '''    public Task<string> CaptureWindowDiagnosticsAsync(
'''
if 'CaptureCandidatesForWindowAsync' not in text:
    method = '''    public Task<IReadOnlyList<UiElementCandidate>> CaptureCandidatesForWindowAsync(
        nint windowHandle,
        int expectedProcessId,
        int maxCandidates = 360,
        CancellationToken cancellationToken = default)
    {
        if (windowHandle == nint.Zero) return Task.FromResult<IReadOnlyList<UiElementCandidate>>([]);
        return Task.Run(() => CaptureCandidatesForWindow(windowHandle, expectedProcessId, maxCandidates, cancellationToken), cancellationToken);
    }

'''
    text = replace_once(text, public_anchor, method + public_anchor, 'scanner public HWND method')

pattern = re.compile(r'''    private IReadOnlyList<UiElementCandidate> CaptureCandidates\(int maxCandidates, CancellationToken cancellationToken, int\? rootProcessId = null\)\n    \{\n(?P<body>.*?)\n    \}\n\n(?=    private static ActionState ReadActionState)''', re.S)
m = pattern.search(text)
if not m:
    raise RuntimeError('scanner CaptureCandidates method not found')
body = m.group('body')
marker = '        var interactivePoolLimit = Math.Max(900, maxCandidates * 3);'
idx = body.find(marker)
if idx < 0:
    raise RuntimeError('scanner traversal marker not found')
core = body[idx:]
replacement = '''    private IReadOnlyList<UiElementCandidate> CaptureCandidates(int maxCandidates, CancellationToken cancellationToken, int? rootProcessId = null)
    {
        var root = AutomationElement.RootElement;
        var walker = TreeWalker.ControlViewWalker;
        var queue = new Queue<(AutomationElement Element, int Depth)>();
        if (rootProcessId is > 0) EnqueueProcessSurfaceRoots(rootProcessId.Value, queue);
        else EnqueueChildren(walker, root, 0, queue);
        return CaptureCandidatesFromQueue(queue, walker, maxCandidates, cancellationToken);
    }

    private IReadOnlyList<UiElementCandidate> CaptureCandidatesForWindow(
        nint windowHandle,
        int expectedProcessId,
        int maxCandidates,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (windowHandle == nint.Zero) return [];

        AutomationElement? root;
        try
        {
            root = AutomationElement.FromHandle((IntPtr)windowHandle);
            if (root is null) return [];
            var rootProcessId = root.Current.ProcessId;
            if (expectedProcessId > 0 && rootProcessId > 0 && rootProcessId != expectedProcessId) return [];
        }
        catch (ElementNotAvailableException) { return []; }
        catch (InvalidOperationException) { return []; }

        var walker = TreeWalker.ControlViewWalker;
        var queue = new Queue<(AutomationElement Element, int Depth)>();
        queue.Enqueue((root, 0));
        return CaptureCandidatesFromQueue(queue, walker, maxCandidates, cancellationToken);
    }

    private IReadOnlyList<UiElementCandidate> CaptureCandidatesFromQueue(
        Queue<(AutomationElement Element, int Depth)> queue,
        TreeWalker walker,
        int maxCandidates,
        CancellationToken cancellationToken)
    {
''' + core + '''
    }

'''
text = text[:m.start()] + replacement + text[m.end():]
write(path, text)

# 4) Shared foreground structural capture: preserve process scan as normal path, supplement weak
# snapshots with the verified foreground HWND, and allow local-choice resolution to prefer HWND.
structural = r'''using HelpSys.Models;
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
'''
write('src/HelpSys.Desktop/MainWindow.StructuralCapture.cs', structural)

# Use the shared capture in the primary quality path and structured fallback.
path = 'src/HelpSys.Desktop/MainWindow.QualityFirst.cs'
text = read(path)
text = text.replace('await _scanner.CaptureCandidatesForProcessAsync(systemContext.ForegroundProcessId, 420, cancellationToken)',
                    'await CaptureForegroundCandidatesAsync(systemContext, 420, cancellationToken)')
text = text.replace('await _scanner.CaptureCandidatesForProcessAsync(expectedContext.ForegroundProcessId, 420, cancellationToken)',
                    'await CaptureForegroundCandidatesAsync(expectedContext, 420, cancellationToken)')
write(path, text)

# 5) Contracts for generic visible-choice and HWND fallback behavior.
choice_test = r'''import fs from 'node:fs';

const main = fs.readFileSync('src/HelpSys.Desktop/MainWindow.xaml.cs', 'utf8');
const live = fs.readFileSync('src/HelpSys.Desktop/MainWindow.LiveGuidance.cs', 'utf8');
const local = fs.readFileSync('src/HelpSys.Desktop/MainWindow.LocalChoiceResolver.cs', 'utf8');
const reliability = fs.readFileSync('src/HelpSys.Desktop/MainWindow.ReliabilityV3.cs', 'utf8');

for (const [source, answer, label] of [
  [main, 'text', 'legacy RequestBox'],
  [live, 'answer', 'AnswerBox']
]) {
  const hook = source.indexOf(`TryHandleLocalVisibleChoiceAnswerAsync(${answer})`);
  const history = source.indexOf(`new GuideHistoryItem(_stepNumber, "clarification_answer", ${answer}`);
  if (hook < 0 || history < 0 || hook > history)
    throw new Error(`${label} clarification path must intercept visible-choice answers before cloud/history handling`);
}

if (!local.includes('LocalVisibleChoiceQuestionRegex') || !local.includes('LooksLikeVisibleChoiceQuestion'))
  throw new Error('local resolver must detect generic visible-choice clarifications');
if (!local.includes('Button", "ListItem", "MenuItem", "Hyperlink", "TabItem", "ComboBox"') ||
    !local.includes('"CheckBox", "RadioButton", "TreeItem"'))
  throw new Error('generic local choice must cover common choice/list/menu/dialog control types');
if (local.includes('IsSupportedLocalChoiceBrowser'))
  throw new Error('generic visible-choice resolution must not be browser-whitelisted');
if (!local.includes('preferWindowScope: true'))
  throw new Error('local visible-choice resolution must prefer the current foreground window scope');
if (!local.includes('FindUniqueLocalVisibleChoice') || !local.includes('RevalidateCandidateAsync(match, context.ForegroundProcessId'))
  throw new Error('generic local choice must require a unique match and revalidate it');
if (!local.includes('利用者が選んだ項目') || local.includes('_activeRequest +=') || local.includes('clarification_answer", answer'))
  throw new Error('local visible-choice answers must remain opaque to cloud-bound request/history');
if (!local.includes('x.Bounds.Contains(center)') || !local.includes('mapped.Count == 1 ? mapped.Values.Single() : null'))
  throw new Error('context-text mapping must use geometric containment and fail closed on ambiguity');
if (!local.includes('if (normalizedAnswer.Length < 3) return null'))
  throw new Error('short answers must not use fuzzy/partial matching');
if (!reliability.includes('_localChoiceTargetActive') || !reliability.includes('利用者が選んだ項目'))
  throw new Error('later action history must keep every locally resolved choice opaque');

console.log('HelpSys generic visible-choice privacy contract passed.');
'''
write('tests/local-choice-contract.mjs', choice_test)

window_test = r'''import fs from 'node:fs';

const scanner = fs.readFileSync('src/HelpSys.Desktop/Services/UiAutomationScanner.cs', 'utf8');
const capture = fs.readFileSync('src/HelpSys.Desktop/MainWindow.StructuralCapture.cs', 'utf8');
const quality = fs.readFileSync('src/HelpSys.Desktop/MainWindow.QualityFirst.cs', 'utf8');

if (!scanner.includes('CaptureCandidatesForWindowAsync') || !scanner.includes('AutomationElement.FromHandle'))
  throw new Error('UIA scanner must support verified HWND-scoped capture');
if (!scanner.includes('rootProcessId != expectedProcessId'))
  throw new Error('HWND-scoped capture must reject a root belonging to another process');
if (!scanner.includes('CaptureCandidatesFromQueue'))
  throw new Error('process and HWND scans must share the same candidate filtering/ranking pipeline');
if (!capture.includes('CaptureCandidatesForProcessAsync') || !capture.includes('CaptureCandidatesForWindowAsync'))
  throw new Error('foreground structural capture must retain PID scan and add HWND supplementation');
if (!capture.includes('preferWindowScope || IsWeakStructuralSnapshot(processCandidates)'))
  throw new Error('normal guidance must only supplement weak PID snapshots unless the caller explicitly prefers HWND scope');
if (!capture.includes('StructuralSnapshotStrength(windowCandidates) > StructuralSnapshotStrength(processCandidates)'))
  throw new Error('weak-snapshot supplementation must keep the stronger structural snapshot');
const uses = quality.match(/CaptureForegroundCandidatesAsync\(/g)?.length ?? 0;
if (uses < 2)
  throw new Error('quality guidance and structured fallback must use the shared foreground structural capture path');

console.log('HelpSys HWND-scoped UIA fallback contract passed.');
'''
write('tests/window-scoped-uia-contract.mjs', window_test)

# Wire the new permanent regression into normal npm check/test.
path = 'package.json'
pkg = json.loads(read(path))
for key, command in [
    ('check', 'node --check tests/window-scoped-uia-contract.mjs'),
    ('test', 'node tests/window-scoped-uia-contract.mjs')
]:
    if command not in pkg['scripts'][key]:
        anchor = 'node --check tests/browser-uia-diagnostics-contract.mjs' if key == 'check' else 'node tests/browser-uia-diagnostics-contract.mjs'
        pkg['scripts'][key] = pkg['scripts'][key].replace(anchor, anchor + ' && ' + command)
write(path, json.dumps(pkg, ensure_ascii=False, indent=2) + '\n')

print('Applied generic visible-choice and HWND-scoped UIA hardening.')

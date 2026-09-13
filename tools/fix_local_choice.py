from pathlib import Path


def replace_once_if_needed(text: str, old: str, new: str, marker: str, label: str) -> str:
    if marker in text:
        return text
    if old not in text:
        raise SystemExit(f'{label} anchor not found')
    return text.replace(old, new, 1)


# Route account/profile clarification answers through a local resolver before raw text
# can enter history or the cloud-bound active request.
main_path = Path('src/HelpSys.Desktop/MainWindow.xaml.cs')
main = main_path.read_text(encoding='utf-8')
old = '''        if (_awaitingClarification && _activeRequest is not null && _sessionCts is not null)\n        {\n            _history.Add(new GuideHistoryItem(_stepNumber, "clarification_answer", text, _clarificationQuestion ?? "確認質問"));\n'''
new = '''        if (_awaitingClarification && _activeRequest is not null && _sessionCts is not null)\n        {\n            var localChoice = await TryHandleLocalAccountChoiceAnswerAsync(text);\n            if (localChoice == LocalChoiceAnswerResult.Handled) return;\n\n            _history.Add(new GuideHistoryItem(_stepNumber, "clarification_answer", text, _clarificationQuestion ?? "確認質問"));\n'''
main = replace_once_if_needed(main, old, new, 'TryHandleLocalAccountChoiceAnswerAsync(text)', 'clarification local-choice hook')

# Reset the local-choice privacy marker on full session reset.
old_end = '''        _forceVisionNext = false;\n        _clarificationQuestion = null;\n        _actionObserver.Stop();\n'''
new_end = '''        _forceVisionNext = false;\n        _clarificationQuestion = null;\n        _localChoiceTargetActive = false;\n        _actionObserver.Stop();\n'''
main = replace_once_if_needed(main, old_end, new_end, '_localChoiceTargetActive = false;\n        _actionObserver.Stop();', 'session local-choice reset')
main_path.write_text(main, encoding='utf-8')


# Ensure successful local account selection never places the raw account label into history.
rel_path = Path('src/HelpSys.Desktop/MainWindow.ReliabilityV3.cs')
rel = rel_path.read_text(encoding='utf-8')
old_target = '''        var decision = _currentDecision;\n        var targetName = _currentTarget is null ? (decision.Key ?? "キーボード操作") : DisplayName(_currentTarget.Name, _currentTarget.ControlType);\n'''
new_target = '''        var decision = _currentDecision;\n        var targetName = _localChoiceTargetActive\n            ? "利用者が選んだアカウント"\n            : _currentTarget is null ? (decision.Key ?? "キーボード操作") : DisplayName(_currentTarget.Name, _currentTarget.ControlType);\n'''
rel = replace_once_if_needed(rel, old_target, new_target, 'var targetName = _localChoiceTargetActive', 'local-choice history label')

old_clear = '''        _v3TrackedDecision = null;\n        _v3TypeActivityObserved = false;\n    }\n'''
new_clear = '''        _v3TrackedDecision = null;\n        _v3TypeActivityObserved = false;\n        _localChoiceTargetActive = false;\n    }\n'''
rel = replace_once_if_needed(rel, old_clear, new_clear, '_v3TypeActivityObserved = false;\n        _localChoiceTargetActive = false;', 'local-choice guidance reset')
rel_path.write_text(rel, encoding='utf-8')


resolver_path = Path('src/HelpSys.Desktop/MainWindow.LocalChoiceResolver.cs')
if not resolver_path.exists():
    resolver_path.write_text(r'''using System.Text;
using System.Text.RegularExpressions;
using HelpSys.Models;
using HelpSys.Services;

namespace HelpSys;

public partial class MainWindow
{
    private static readonly Regex LocalAccountChoiceSurfaceRegex = new(
        @"どなたが使用|プロファイル.*選|プロフィール.*選|アカウント(?:の)?選択|アカウント.*選|ユーザー.*選|別のアカウントを使用|ゲストモード|choose\s+an?\s+account|select\s+an?\s+account|who(?:'s|\s+is)\s+using\s+chrome|use\s+another\s+account|guest\s+mode",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex LocalChoiceUtilityRegex = new(
        @"^(?:ゲストモード|guest\s+mode|別のアカウントを使用|use\s+another\s+account|アカウントを追加|add\s+account|その他|more|設定|settings|閉じる|close)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private bool _localChoiceTargetActive;

    private enum LocalChoiceAnswerResult
    {
        NotApplicable,
        Handled
    }

    private async Task<LocalChoiceAnswerResult> TryHandleLocalAccountChoiceAnswerAsync(string answer)
    {
        if (!LooksLikeAccountChoiceQuestion(_clarificationQuestion))
            return LocalChoiceAnswerResult.NotApplicable;

        if (_sessionCts is null || _sessionCts.IsCancellationRequested || _activeRequest is null)
            return LocalChoiceAnswerResult.Handled;

        var token = _sessionCts.Token;
        var context = _systemContext.Capture();
        if (!HasUsableForeground(context) || !IsSupportedLocalChoiceBrowser(context.ForegroundProcess))
        {
            KeepLocalAccountChoiceClarification(
                "アカウント選択画面を端末内で確認できませんでした。選択画面を表示したまま、画面に出ている名前をもう一度入力してください。");
            return LocalChoiceAnswerResult.Handled;
        }

        IReadOnlyList<UiElementCandidate> candidates;
        try
        {
            candidates = await _scanner.CaptureCandidatesForProcessAsync(context.ForegroundProcessId, 420, token);
        }
        catch (OperationCanceledException) { return LocalChoiceAnswerResult.Handled; }
        catch
        {
            KeepLocalAccountChoiceClarification(
                "アカウント選択画面を端末内で読み取れませんでした。選択画面を表示したまま、もう一度同じ名前を入力してください。");
            return LocalChoiceAnswerResult.Handled;
        }

        if (!LooksLikeLocalAccountChoiceSurface(context, candidates))
        {
            KeepLocalAccountChoiceClarification(
                "アカウント選択画面を端末内で確認できませんでした。選択画面を表示したまま、画面に出ている名前をもう一度入力してください。");
            return LocalChoiceAnswerResult.Handled;
        }

        var match = FindUniqueLocalAccountChoice(candidates, answer);
        if (match is null)
        {
            KeepLocalAccountChoiceClarification(
                "回答と画面上のアカウントを1つに特定できませんでした。画面に表示されている名前またはメールアドレスを、そのまま1つ入力してください。");
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
            KeepLocalAccountChoiceClarification(
                "選んだアカウントの表示位置が変わりました。現在の選択画面に出ている名前をもう一度入力してください。");
            return LocalChoiceAnswerResult.Handled;
        }

        var generation = _sessionState.Generation;
        if (!_sessionState.TryTransition(generation, GuidanceSessionState.Capturing) ||
            !_sessionState.TryTransition(generation, GuidanceSessionState.Planning))
        {
            KeepLocalAccountChoiceClarification(
                "アカウント選択の案内状態を更新できませんでした。現在の選択画面に出ている名前をもう一度入力してください。");
            return LocalChoiceAnswerResult.Handled;
        }

        _history.Add(new GuideHistoryItem(
            _stepNumber,
            "local_choice_answer",
            "利用者が選んだアカウント",
            "利用者の回答を端末内だけで現在の選択肢と照合し、回答内容を外部送信せず対象を確定した。"));
        if (_history.Count > 12) _history.RemoveAt(0);

        _clarificationQuestion = null;
        GuideButton.Content = "案内";
        RequestBox.Text = _originalRequest ?? _activeRequest;
        RequestBox.CaretIndex = RequestBox.Text.Length;
        _localChoiceTargetActive = true;
        try { _actionObserver.Start(); } catch { }

        var decision = new GuideDecision(
            "target",
            fresh.Id,
            "left_click",
            "青い枠の、あなたが選んだアカウントで、マウスの左ボタンを1回押してください。",
            null,
            null,
            0.99);

        ShowStructuredTarget(decision, fresh, candidates, context, generation);
        return LocalChoiceAnswerResult.Handled;
    }

    private void KeepLocalAccountChoiceClarification(string question)
    {
        _localChoiceTargetActive = false;
        WaitForClarification(question);
    }

    private static bool LooksLikeAccountChoiceQuestion(string? question)
    {
        if (string.IsNullOrWhiteSpace(question)) return false;
        return question.Contains("アカウント", StringComparison.OrdinalIgnoreCase) ||
               question.Contains("プロフィール", StringComparison.OrdinalIgnoreCase) ||
               question.Contains("プロファイル", StringComparison.OrdinalIgnoreCase) ||
               question.Contains("使う人", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSupportedLocalChoiceBrowser(string processName) =>
        processName.Equals("chrome", StringComparison.OrdinalIgnoreCase) ||
        processName.Equals("msedge", StringComparison.OrdinalIgnoreCase) ||
        processName.Equals("firefox", StringComparison.OrdinalIgnoreCase) ||
        processName.Equals("brave", StringComparison.OrdinalIgnoreCase) ||
        processName.Equals("opera", StringComparison.OrdinalIgnoreCase) ||
        processName.Equals("vivaldi", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeLocalAccountChoiceSurface(
        SystemContextSnapshot context,
        IReadOnlyList<UiElementCandidate> candidates)
    {
        var builder = new StringBuilder(context.ForegroundTitle ?? string.Empty);
        foreach (var candidate in candidates.Take(220))
        {
            if (!string.IsNullOrWhiteSpace(candidate.Name))
                builder.Append(' ').Append(candidate.Name);
        }
        return LocalAccountChoiceSurfaceRegex.IsMatch(builder.ToString());
    }

    private static UiElementCandidate? FindUniqueLocalAccountChoice(
        IReadOnlyList<UiElementCandidate> candidates,
        string answer)
    {
        var normalizedAnswer = NormalizeLocalChoiceText(answer);
        if (string.IsNullOrWhiteSpace(normalizedAnswer)) return null;

        var interactable = candidates
            .Where(x => x.Interactable && x.Enabled && !x.Bounds.IsEmpty && !string.IsNullOrWhiteSpace(x.Name))
            .ToArray();

        var exact = interactable
            .Where(x => NormalizeLocalChoiceText(x.Name).Equals(normalizedAnswer, StringComparison.Ordinal))
            .ToArray();
        if (exact.Length == 1) return exact[0];
        if (exact.Length > 1) return null;

        var partial = interactable
            .Where(x => !LocalChoiceUtilityRegex.IsMatch(x.Name.Trim()))
            .Where(x =>
            {
                var name = NormalizeLocalChoiceText(x.Name);
                return name.Length > 0 &&
                       (name.Contains(normalizedAnswer, StringComparison.Ordinal) ||
                        normalizedAnswer.Contains(name, StringComparison.Ordinal));
            })
            .ToArray();

        return partial.Length == 1 ? partial[0] : null;
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
''', encoding='utf-8')


# Static contract: raw answer must be intercepted before the old cloud/history path,
# local matching must require a unique current UIA target, and logs must stay opaque.
contract_path = Path('tests/local-choice-contract.mjs')
if not contract_path.exists():
    contract_path.write_text(r'''import fs from 'node:fs';

const main = fs.readFileSync('src/HelpSys.Desktop/MainWindow.xaml.cs', 'utf8');
const local = fs.readFileSync('src/HelpSys.Desktop/MainWindow.LocalChoiceResolver.cs', 'utf8');
const reliability = fs.readFileSync('src/HelpSys.Desktop/MainWindow.ReliabilityV3.cs', 'utf8');

const hook = main.indexOf('TryHandleLocalAccountChoiceAnswerAsync(text)');
const cloudHistory = main.indexOf('new GuideHistoryItem(_stepNumber, "clarification_answer", text');
if (hook < 0 || cloudHistory < 0 || hook > cloudHistory)
  throw new Error('local account resolver must intercept the raw clarification answer before cloud/history handling');

if (!local.includes('FindUniqueLocalAccountChoice'))
  throw new Error('local account resolver must require a unique local UIA match');
if (!local.includes('RevalidateCandidateAsync(match, context.ForegroundProcessId'))
  throw new Error('local account target must be revalidated before guidance');
if (!local.includes('利用者が選んだアカウント'))
  throw new Error('local account history must use an opaque label');
if (local.includes('_activeRequest +=') || local.includes('clarification_answer", answer'))
  throw new Error('local resolver must not append the raw account answer to the cloud-bound request/history');
if (!reliability.includes('_localChoiceTargetActive') || !reliability.includes('利用者が選んだアカウント'))
  throw new Error('successful local account selection must keep later history opaque');

console.log('HelpSys local account-choice privacy contract passed.');
''', encoding='utf-8')

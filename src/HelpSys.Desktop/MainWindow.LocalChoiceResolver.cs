using System.Text;
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

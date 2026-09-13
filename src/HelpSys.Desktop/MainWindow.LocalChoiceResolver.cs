using System.Text;
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

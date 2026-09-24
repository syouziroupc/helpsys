using System.Text;
using System.Text.RegularExpressions;
using HelpSys.Models;
using HelpSys.Services;

namespace HelpSys;

public partial class MainWindow
{
    private static readonly Regex LocalVisibleChoiceQuestionRegex = new(
        @"(?:どれ(?:を|が)?|どちら(?:を|が)?|どの(?:項目|ボタン|リンク|タブ|アカウント|プロフィール|プロファイル|ファイル|フォルダ|プリンタ(?:ー)?|ネットワーク|候補)|画面(?:上|内).*(?:選択|選ん|どれ|どちら)|一覧.*(?:選択|選ん|どれ|どちら)|(?:選んで|選択して|使いたいものを|使いたい方を).*(?:ください|教えて)|which\s+(?:one|item|button|link|tab|account|profile|file|folder|printer|network)|choose\s+(?:one|an?\s+item|an?\s+account|an?\s+profile)|select\s+(?:one|an?\s+item|an?\s+account|an?\s+profile)|pick\s+(?:one|an?\s+item))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private bool _localChoiceTargetActive;

    private enum LocalChoiceAnswerResult
    {
        NotApplicable,
        Handled
    }

    // Compatibility entry point used by both clarification UIs. The implementation is intentionally
    // generic: account/profile choosers are only one instance of a visible-choice surface.
    private Task<LocalChoiceAnswerResult> TryHandleLocalAccountChoiceAnswerAsync(string answer)
        => TryHandleLocalVisibleChoiceAnswerAsync(answer);

    private bool TryPresentOutlawVisibleChoiceButtons(
        IReadOnlyList<UiElementCandidate> candidates,
        SystemContextSnapshot context,
        long generation)
    {
        if (!OutlawModePolicy.Enabled ||
            !_sessionState.IsCurrent(generation) ||
            !HasUsableForeground(context))
            return false;

        var choices = candidates
            .Where(x => x.Interactable && x.Enabled && !x.Bounds.IsEmpty && !string.IsNullOrWhiteSpace(x.Name))
            .Where(x => context.ForegroundProcessId <= 0 || x.ProcessId <= 0 || x.ProcessId == context.ForegroundProcessId)
            .Where(x => !Regex.IsMatch(
                x.Name,
                @"^(閉じる|戻る|キャンセル|次へ|追加|設定|メニュー|その他|close|back|cancel|next|add|settings?|menu|more)$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            .GroupBy(x => NormalizeLocalChoiceText(x.Name), StringComparer.Ordinal)
            .Where(g => !string.IsNullOrWhiteSpace(g.Key) && g.Count() == 1)
            .Select(g => g.Single())
            .Take(9)
            .ToArray();

        return PresentOutlawChoiceButtons(
            choices,
            context,
            generation,
            "画面上の候補から、使いたいものを1つ選んでください。",
            "画面に表示されている候補から、使いたいものを直接選んでください。",
            "outlaw_direct_choice");
    }

    private bool TryAutoSelectOutlawIdentityChoice(
        IReadOnlyList<UiElementCandidate> candidates,
        SystemContextSnapshot context,
        long generation)
    {
        if (!OutlawModePolicy.Enabled ||
            !_sessionState.IsCurrent(generation) ||
            !HasUsableForeground(context))
            return false;

        var choices = candidates
            .Where(x => x.Interactable && x.Enabled && !x.Bounds.IsEmpty)
            .Where(x => x.ProcessId == context.ForegroundProcessId)
            .Where(x =>
                x.AutomationId.Equals("profileCardButton", StringComparison.OrdinalIgnoreCase) ||
                Regex.IsMatch(x.Name ?? string.Empty, @"(?:プロフィール|profile).*(?:開く|open)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            .GroupBy(x => NormalizeLocalChoiceText(x.Name), StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(x => x.Y)
            .ThenBy(x => x.X)
            .Take(9)
            .ToArray();

        if (choices.Length < 2) return false;

        var request = _activeRequest ?? string.Empty;
        var selected = choices.FirstOrDefault(x =>
            !string.IsNullOrWhiteSpace(x.Name) &&
            request.Contains(
                Regex.Replace(x.Name, @"(?:\s*のプロフィールを開く|\s*profile.*)$", string.Empty, RegexOptions.IgnoreCase).Trim(),
                StringComparison.OrdinalIgnoreCase))
            ?? choices[0];

        if (!_sessionState.TryTransition(generation, GuidanceSessionState.Planning)) return false;

        var label = DisplayName(selected.Name, selected.ControlType);
        var instruction = $"候補が{choices.Length}つあります。今回は「{label}」を選びます。青い枠の項目を1回押してください。";
        var decision = new GuideDecision(
            "target",
            selected.Id,
            "left_click",
            instruction,
            null,
            null,
            0.99);

        LocalLogService.Write(
            "outlaw_identity_auto_selected",
            $"choices={choices.Length};selected={selected.Id};name={label};foreground={context.ForegroundProcess}/{context.ForegroundProcessId}");

        ShowStructuredTarget(decision, selected, candidates, context, generation);
        return true;
    }

    private bool PresentOutlawChoiceButtons(
        IReadOnlyList<UiElementCandidate> choices,
        SystemContextSnapshot context,
        long generation,
        string question,
        string stateText,
        string logEvent)
    {
        if (choices.Count < 2) return false;
        if (!_sessionState.TryTransition(generation, GuidanceSessionState.Clarifying)) return false;

        _technicalClarificationRetries = 0;
        _overlay.Hide();
        _keyHint.Hide();
        _actionObserver.Stop();
        _currentDecision = null;
        _currentTarget = null;
        _guidedBounds = null;
        _clarificationQuestion = question;
        _lastInstruction = question;

        ChoiceButtonsPanel.Children.Clear();
        foreach (var choice in choices)
        {
            var button = new System.Windows.Controls.Button
            {
                Content = choice.Name,
                Tag = choice.Name,
                Margin = new System.Windows.Thickness(0, 0, 7, 7),
                Padding = new System.Windows.Thickness(10, 5, 10, 5),
                MinHeight = 32,
                MaxWidth = 280
            };
            button.Click += LocalVisibleChoiceButton_Click;
            ChoiceButtonsPanel.Children.Add(button);
        }

        ClarificationQuestionText.Text = question;
        AnswerEntryPanel.Visibility = System.Windows.Visibility.Collapsed;
        ChoiceButtonsPanel.Visibility = System.Windows.Visibility.Visible;
        ClarificationPanel.Visibility = System.Windows.Visibility.Visible;
        RequestBox.IsReadOnly = true;
        GuideButton.Content = "案内";
        GuideButton.IsEnabled = false;
        VoiceButton.IsEnabled = false;
        UpdateLayout();
        PositionNearBottomRight();

        LocalLogService.Write(
            logEvent,
            $"choices={choices.Count};foreground={context.ForegroundProcess}/{context.ForegroundProcessId}");
        SetState(stateText, speak: false);
        return true;
    }

    private async Task TryShowLocalVisibleChoiceButtonsAsync()
    {
        if (!_awaitingClarification || !LooksLikeVisibleChoiceQuestion(_clarificationQuestion) ||
            _sessionCts is null || _sessionCts.IsCancellationRequested)
        {
            ChoiceButtonsPanel.Visibility = System.Windows.Visibility.Collapsed;
            AnswerEntryPanel.Visibility = System.Windows.Visibility.Visible;
            return;
        }

        var token = _sessionCts.Token;
        var context = _systemContext.Capture();
        if (!HasUsableForeground(context)) return;

        IReadOnlyList<UiElementCandidate> candidates;
        try
        {
            candidates = await _scanner.CaptureCandidatesForProcessAsync(context.ForegroundProcessId, 420, token);
        }
        catch
        {
            return;
        }

        if (token.IsCancellationRequested || !_awaitingClarification) return;

        var choices = candidates
            .Where(x => x.Interactable && x.Enabled && !x.Bounds.IsEmpty && !string.IsNullOrWhiteSpace(x.Name))
            .Where(x => context.ForegroundProcessId <= 0 || x.ProcessId <= 0 || x.ProcessId == context.ForegroundProcessId)
            .Where(x => !Regex.IsMatch(x.Name, @"^(閉じる|戻る|キャンセル|次へ|追加|設定|メニュー|その他|close|back|cancel|next|add|settings?|menu|more)$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            .GroupBy(x => NormalizeLocalChoiceText(x.Name), StringComparer.Ordinal)
            .Where(g => !string.IsNullOrWhiteSpace(g.Key) && g.Count() == 1)
            .Select(g => g.Single())
            .Take(9)
            .ToArray();

        if (choices.Length < 2) return;

        ChoiceButtonsPanel.Children.Clear();
        foreach (var choice in choices)
        {
            var button = new System.Windows.Controls.Button
            {
                Content = choice.Name,
                Tag = choice.Name,
                Margin = new System.Windows.Thickness(0, 0, 7, 7),
                Padding = new System.Windows.Thickness(10, 5, 10, 5),
                MinHeight = 32,
                MaxWidth = 280
            };
            button.Click += LocalVisibleChoiceButton_Click;
            ChoiceButtonsPanel.Children.Add(button);
        }

        ClarificationQuestionText.Text = "画面に表示されている候補から、使いたいものを直接選んでください。";
        AnswerEntryPanel.Visibility = System.Windows.Visibility.Collapsed;
        ChoiceButtonsPanel.Visibility = System.Windows.Visibility.Visible;
        UpdateLayout();
        PositionNearBottomRight();
    }

    private async void LocalVisibleChoiceButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button || button.Tag is not string answer ||
            string.IsNullOrWhiteSpace(answer))
            return;

        button.IsEnabled = false;
        var result = await TryHandleLocalVisibleChoiceAnswerAsync(answer);
        if (result != LocalChoiceAnswerResult.Handled) button.IsEnabled = true;
    }

    private async Task<LocalChoiceAnswerResult> TryHandleLocalVisibleChoiceAnswerAsync(string answer)
    {
        if (!LooksLikeVisibleChoiceQuestion(_clarificationQuestion))
            return LocalChoiceAnswerResult.NotApplicable;

        if (_sessionCts is null || _sessionCts.IsCancellationRequested || _activeRequest is null)
            return LocalChoiceAnswerResult.Handled;

        var token = _sessionCts.Token;
        var context = _systemContext.Capture();
        if (!HasUsableForeground(context))
        {
            KeepLocalVisibleChoiceClarification(
                "選択肢が表示されている画面を端末内で確認できませんでした。選択画面を表示したまま、見えている項目名をもう一度入力してください。");
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
            KeepLocalVisibleChoiceClarification(
                "選択肢を端末内で読み取れませんでした。選択画面を表示したまま、もう一度同じ項目名を入力してください。");
            return LocalChoiceAnswerResult.Handled;
        }

        var match = FindUniqueLocalVisibleChoice(candidates, answer);
        if (match is null)
        {
            // Only intercept when the current UI actually looks like a visible choice surface.
            // Open-ended clarification answers continue through the normal planner path.
            if (!LooksLikeVisibleChoiceSurface(candidates))
                return LocalChoiceAnswerResult.NotApplicable;

            KeepLocalVisibleChoiceClarification(
                "回答と画面上の選択肢を1つに特定できませんでした。画面に表示されている項目名を、そのまま1つ入力してください。");
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
            KeepLocalVisibleChoiceClarification(
                "選んだ項目の位置または画面が変わりました。現在表示されている項目名をもう一度入力してください。");
            return LocalChoiceAnswerResult.Handled;
        }

        var generation = _sessionState.Generation;
        if (!_sessionState.TryTransition(generation, GuidanceSessionState.Capturing) ||
            !_sessionState.TryTransition(generation, GuidanceSessionState.Planning))
        {
            KeepLocalVisibleChoiceClarification(
                "選択案内の状態を更新できませんでした。現在表示されている項目名をもう一度入力してください。");
            return LocalChoiceAnswerResult.Handled;
        }

        _history.Add(new GuideHistoryItem(
            _stepNumber,
            "local_choice_answer",
            "利用者が選んだ項目",
            "利用者の回答を端末内だけで現在の可視選択肢と照合し、回答内容を外部送信せず対象を確定した。"));
        if (_history.Count > 12) _history.RemoveAt(0);

        var accountSpecific = LooksLikeAccountChoiceQuestion(_clarificationQuestion);
        _clarificationQuestion = null;
        HideClarificationUiIfNeeded(force: true);
        RequestBox.Text = _originalRequest ?? _activeRequest;
        RequestBox.CaretIndex = RequestBox.Text.Length;
        _localChoiceTargetActive = accountSpecific;
        try { _actionObserver.Start(); } catch { }

        // Generic visible choices must stay opaque in later cloud-bound history. Account choices
        // already use the legacy opaque-history flag; other choices carry an intentionally generic
        // local-only label while preserving geometry/automation identity for revalidation.
        var trackedTarget = accountSpecific
            ? fresh
            : fresh with { Name = "利用者が選んだ項目" };

        var decision = new GuideDecision(
            "target",
            fresh.Id,
            "left_click",
            "青い枠の、あなたが選んだ項目で、マウスの左ボタンを1回押してください。",
            null,
            null,
            0.99);

        ShowStructuredTarget(decision, trackedTarget, candidates, context, generation);
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
        return LocalVisibleChoiceQuestionRegex.IsMatch(question) || LooksLikeAccountChoiceQuestion(question);
    }

    private static bool LooksLikeAccountChoiceQuestion(string? question)
    {
        if (string.IsNullOrWhiteSpace(question)) return false;
        return question.Contains("アカウント", StringComparison.OrdinalIgnoreCase) ||
               question.Contains("プロフィール", StringComparison.OrdinalIgnoreCase) ||
               question.Contains("プロファイル", StringComparison.OrdinalIgnoreCase) ||
               question.Contains("使う人", StringComparison.OrdinalIgnoreCase) ||
               question.Contains("account", StringComparison.OrdinalIgnoreCase) ||
               question.Contains("profile", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeVisibleChoiceSurface(IReadOnlyList<UiElementCandidate> candidates)
    {
        var interactable = candidates.Count(x => x.Interactable && x.Enabled && !x.Bounds.IsEmpty);
        if (interactable >= 2) return true;

        var visibleLabels = candidates.Count(x =>
            !x.Interactable && x.Enabled && !x.Bounds.IsEmpty && !string.IsNullOrWhiteSpace(x.Name));
        return interactable >= 1 && visibleLabels >= 2;
    }

    private static UiElementCandidate? FindUniqueLocalVisibleChoice(
        IReadOnlyList<UiElementCandidate> candidates,
        string answer)
    {
        var normalizedAnswer = NormalizeLocalChoiceText(answer);
        if (string.IsNullOrWhiteSpace(normalizedAnswer)) return null;

        var interactable = candidates
            .Where(x => x.Interactable && x.Enabled && !x.Bounds.IsEmpty)
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

        if (normalizedAnswer.Length < 2) return null;
        var partial = interactable
            .Where(x =>
            {
                var name = NormalizeLocalChoiceText(x.Name);
                return name.Length >= 2 &&
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
        // Many applications expose a visible label as a non-interactive child while the clickable
        // parent has no useful name. Map a matching label only to the smallest containing clickable
        // element. Never use nearest-neighbour guessing; ambiguity fails closed.
        if (normalizedAnswer.Length < 2) return null;

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
                .Where(x => context.ProcessId <= 0 || x.ProcessId <= 0 || x.ProcessId == context.ProcessId)
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

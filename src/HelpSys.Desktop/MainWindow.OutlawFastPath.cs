using System.Diagnostics;
using System.Text.RegularExpressions;
using HelpSys.Models;
using HelpSys.Services;

namespace HelpSys;

public partial class MainWindow
{
    private string? _outlawNavigatorRequest;
    private string? _outlawNavigatorTarget;
    private string _outlawNavigatorStage = "idle";

    private async Task<bool> TryOutlawBrowserSearchFastPathAsync(
        IReadOnlyList<UiElementCandidate> candidates,
        SystemContextSnapshot context,
        ObservationSnapshot snapshot,
        long generation,
        CancellationToken cancellationToken)
    {
        if (!OutlawModePolicy.Enabled ||
            !_sessionState.IsCurrent(generation) ||
            string.IsNullOrWhiteSpace(_activeRequest))
            return false;

        // The same entry point now handles both browser search and Windows shell search.  Keeping
        // it here preserves the existing Fast Lane call order while allowing unknown applications
        // to use Windows Search without adding app-specific aliases.
        if (IsWindowsShellProcessName(context.ForegroundProcess))
        {
            return await TryOutlawUniversalShellNavigationAsync(
                candidates,
                context,
                snapshot,
                generation,
                cancellationToken);
        }

        if (!IsBrowserProcessForFastPath(context.ForegroundProcess)) return false;
        return await TryOutlawVerifiedWebSearchFastPathAsync(
            candidates,
            context,
            snapshot,
            generation,
            cancellationToken);
    }

    private async Task<bool> TryOutlawVerifiedWebSearchFastPathAsync(
        IReadOnlyList<UiElementCandidate> candidates,
        SystemContextSnapshot context,
        ObservationSnapshot snapshot,
        long generation,
        CancellationToken cancellationToken)
    {
        if (_activeRequest is null) return false;

        var searchText = ResolveBeginnerWebSearchText(_activeRequest) ?? ResolveGenericOpenTarget(_activeRequest);
        if (string.IsNullOrWhiteSpace(searchText)) return false;
        EnsureNavigatorTransaction(_activeRequest, searchText);

        var searchField = candidates
            .Where(x =>
                x.ProcessId == context.ForegroundProcessId &&
                x.Interactable &&
                x.Enabled &&
                x.KeyboardFocusable &&
                !x.Bounds.IsEmpty &&
                (x.ControlType.Equals("Edit", StringComparison.OrdinalIgnoreCase) ||
                 x.ControlType.Equals("ComboBox", StringComparison.OrdinalIgnoreCase)) &&
                x.Y >= 180 &&
                x.Width >= 220 &&
                (HasSemanticRole(x, "web_search") ||
                 x.Name.Contains("検索", StringComparison.OrdinalIgnoreCase) ||
                 x.Name.Contains("search", StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(x => HasSemanticRole(x, "web_search"))
            .ThenByDescending(x => x.Width * x.Height)
            .FirstOrDefault();

        if (searchField is null) return false;

        var observedQuery = NormalizeNavigatorText(searchField.Value);
        var queryVerified = IsNavigatorQueryVerified(searchText, observedQuery, context);
        LocalLogService.Write(
            "navigator_postcondition",
            $"surface=web_search;expected={searchText};observed={observedQuery};verified={queryVerified.ToString().ToLowerInvariant()};title={context.ForegroundTitle}");

        // History is audit evidence only.  It is never authoritative navigation state.  If the
        // live field/title proves that the requested query is already applied, let the current
        // results surface be handled normally.  Otherwise repair the live field even if history
        // says we previously instructed the same query.
        if (queryVerified)
        {
            SetNavigatorStage("query_verified", $"surface=web;target={searchText}");
            return false;
        }

        var fresh = await RevalidateNavigatorCandidateAsync(searchField, context, snapshot, cancellationToken);
        if (fresh is null) return false;
        if (!_sessionState.IsCurrent(generation)) return true;
        if (!_sessionState.TryTransition(generation, GuidanceSessionState.Planning)) return false;

        var liveValue = NormalizeNavigatorText(fresh.Value);
        GuideDecision decision;
        if (!fresh.Focused)
        {
            decision = new GuideDecision(
                "target",
                fresh.Id,
                "left_click",
                $"URL欄ではなく検索欄を使います。「{DisplayName(fresh.Name, fresh.ControlType)}」を1回押してください。",
                null,
                null,
                0.995);
            SetNavigatorStage("focus_query_field", $"surface=web;target={searchText}");
        }
        else
        {
            var replace = !string.IsNullOrWhiteSpace(liveValue) &&
                          !NavigatorTextEquals(liveValue, searchText);
            var instruction = replace
                ? $"検索欄には現在「{liveValue}」が入っています。Ctrl+Aで全部選択して「{searchText}」に置き換え、最後にEnterキーを1回押してください。"
                : $"検索欄に「{searchText}」と入力して、最後にEnterキーを1回押してください。";
            decision = new GuideDecision(
                "target",
                fresh.Id,
                "type_text",
                instruction,
                null,
                "Enter",
                0.995);
            SetNavigatorStage("apply_query", $"surface=web;target={searchText};replace={replace.ToString().ToLowerInvariant()}");
        }

        LocalLogService.Write(
            "outlaw_local_browser_search",
            $"target={fresh.Id};focused={fresh.Focused};query={searchText};observed={liveValue};bounds={fresh.Bounds};revalidated=true");
        ShowStructuredTarget(decision, fresh, candidates, context, generation);
        return _sessionState.State == GuidanceSessionState.AwaitingUserAction;
    }

    private async Task<bool> TryOutlawUniversalShellNavigationAsync(
        IReadOnlyList<UiElementCandidate> candidates,
        SystemContextSnapshot context,
        ObservationSnapshot snapshot,
        long generation,
        CancellationToken cancellationToken)
    {
        if (_activeRequest is null) return false;
        var targetText = ResolveGenericOpenTarget(_activeRequest);
        if (string.IsNullOrWhiteSpace(targetText)) return false;
        EnsureNavigatorTransaction(_activeRequest, targetText);

        var searchSurfaceOpen = candidates.Any(x =>
            x.ProcessName.Equals("StartMenuExperienceHost", StringComparison.OrdinalIgnoreCase) ||
            x.ProcessName.Equals("SearchHost", StringComparison.OrdinalIgnoreCase) ||
            x.ProcessName.Equals("TextInputHost", StringComparison.OrdinalIgnoreCase) ||
            HasSemanticRole(x, "windows_search"));

        // A currently visible desktop/taskbar/shell target has priority over opening Search.
        // Defer it to the normal structured/vision planner because activation semantics differ
        // between surfaces (for example desktop icons usually need a double-click while taskbar
        // buttons are single-click).  The navigator owns only the fallback when no visible target
        // can be grounded on the current shell surface.
        if (!searchSurfaceOpen)
        {
            var visibleTarget = FindVisibleShellTargetCandidate(candidates, targetText);
            if (visibleTarget is not null)
            {
                SetNavigatorStage(
                    "visible_shell_target",
                    $"surface=windows_shell;target={targetText};candidate={visibleTarget.Name};defer=structured_planner");
                LocalLogService.Write(
                    "navigator_visible_target",
                    $"target={targetText};candidate={visibleTarget.Id};name={visibleTarget.Name};process={visibleTarget.ProcessName}");
                return false;
            }
        }

        // Only treat a visible name as a search result after Search/Start is actually open.  This
        // avoids turning a desktop shortcut into a single-click launch instruction.
        if (searchSurfaceOpen)
        {
            var result = FindNavigatorResultCandidate(candidates, targetText);
            if (result is not null)
            {
                var freshResult = await RevalidateNavigatorCandidateAsync(result, context, snapshot, cancellationToken);
                if (freshResult is not null)
                {
                    if (!_sessionState.IsCurrent(generation)) return true;
                    if (!_sessionState.TryTransition(generation, GuidanceSessionState.Planning)) return false;
                    SetNavigatorStage("activate_result", $"surface=windows_search;target={targetText};candidate={freshResult.Name}");
                    var decision = new GuideDecision(
                        "target",
                        freshResult.Id,
                        "left_click",
                        $"検索結果の「{DisplayName(freshResult.Name, freshResult.ControlType)}」を1回押してください。",
                        null,
                        null,
                        0.995);
                    ShowStructuredTarget(decision, freshResult, candidates, context, generation);
                    return _sessionState.State == GuidanceSessionState.AwaitingUserAction;
                }
            }

            var searchField = FindWindowsSearchField(candidates);
            if (searchField is not null)
            {
                var observed = NormalizeNavigatorText(searchField.Value);
                var verified = NavigatorTextEquals(observed, targetText);
                LocalLogService.Write(
                    "navigator_postcondition",
                    $"surface=windows_search;expected={targetText};observed={observed};verified={verified.ToString().ToLowerInvariant()}");

                // If the exact query is already visible but no actionable result was found, do not
                // type it repeatedly.  Fall through to the normal structured/vision planner.
                if (verified)
                {
                    SetNavigatorStage("query_verified", $"surface=windows_search;target={targetText}");
                    return false;
                }

                var freshField = await RevalidateNavigatorCandidateAsync(searchField, context, snapshot, cancellationToken);
                if (freshField is null) return false;
                if (!_sessionState.IsCurrent(generation)) return true;
                if (!_sessionState.TryTransition(generation, GuidanceSessionState.Planning)) return false;

                var liveValue = NormalizeNavigatorText(freshField.Value);
                GuideDecision decision;
                if (!freshField.Focused)
                {
                    decision = new GuideDecision(
                        "target",
                        freshField.Id,
                        "left_click",
                        $"Windows検索欄を1回押してください。次に「{targetText}」を検索します。",
                        null,
                        null,
                        0.995);
                    SetNavigatorStage("focus_query_field", $"surface=windows_search;target={targetText}");
                }
                else
                {
                    var replace = !string.IsNullOrWhiteSpace(liveValue) && !NavigatorTextEquals(liveValue, targetText);
                    var instruction = replace
                        ? $"Windows検索欄には現在「{liveValue}」が入っています。Ctrl+Aで全部選択して「{targetText}」に置き換えてください。"
                        : $"Windows検索欄に「{targetText}」と入力してください。";
                    decision = new GuideDecision(
                        "target",
                        freshField.Id,
                        "type_text",
                        instruction,
                        null,
                        "Enter",
                        0.995);
                    SetNavigatorStage("apply_query", $"surface=windows_search;target={targetText};replace={replace.ToString().ToLowerInvariant()}");
                }

                ShowStructuredTarget(decision, freshField, candidates, context, generation);
                return _sessionState.State == GuidanceSessionState.AwaitingUserAction;
            }

            SetNavigatorStage("search_surface_unresolved", $"surface=windows_search;target={targetText}");
            return false;
        }

        // Unknown applications reach this path too.  No application whitelist is required: open
        // Windows Search first, then carry targetText across subsequent observations.
        if (!_observationBroker.IsCurrent(snapshot)) return false;
        if (!_sessionState.TryTransition(generation, GuidanceSessionState.Planning)) return false;
        var openSearch = new GuideDecision(
            "target",
            null,
            "press_key",
            $"キーボードのWindowsキーを1回押してください。開いた検索画面で「{targetText}」を探します。",
            null,
            "win",
            0.995);
        SetNavigatorStage("open_search_surface", $"target={targetText}");
        ShowKeyboardGuide(openSearch, candidates, context, generation);
        var presented = _sessionState.State == GuidanceSessionState.AwaitingUserAction;
        LocalLogService.Write(
            "navigator_present",
            $"action=press_key;key=win;target={targetText};result={(presented ? "success" : "failed")}");
        return presented;
    }

    private async Task<UiElementCandidate?> RevalidateNavigatorCandidateAsync(
        UiElementCandidate candidate,
        SystemContextSnapshot context,
        ObservationSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        UiElementCandidate? fresh;
        try
        {
            fresh = await _scanner.RevalidateCandidateAsync(
                candidate,
                candidate.ProcessId > 0 ? candidate.ProcessId : context.ForegroundProcessId,
                cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch { fresh = null; }

        var snapshotCurrent = _observationBroker.IsCurrent(snapshot);
        if (fresh is null || !snapshotCurrent)
        {
            LocalLogService.Write(
                "navigator_revalidate",
                $"target={candidate.Id};fresh={(fresh is null ? "miss" : "hit")};snapshotCurrent={snapshotCurrent.ToString().ToLowerInvariant()}");
            return null;
        }
        return fresh;
    }

    private static UiElementCandidate? FindWindowsSearchField(IReadOnlyList<UiElementCandidate> candidates)
        => candidates
            .Where(x =>
                x.Interactable && x.Enabled && x.KeyboardFocusable && !x.Bounds.IsEmpty &&
                (x.ControlType.Equals("Edit", StringComparison.OrdinalIgnoreCase) ||
                 x.ControlType.Equals("ComboBox", StringComparison.OrdinalIgnoreCase)) &&
                (HasSemanticRole(x, "windows_search") ||
                 ((x.ProcessName.Contains("SearchHost", StringComparison.OrdinalIgnoreCase) ||
                   x.ProcessName.Contains("StartMenuExperienceHost", StringComparison.OrdinalIgnoreCase) ||
                   x.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase)) &&
                  (x.Name.Contains("検索", StringComparison.OrdinalIgnoreCase) ||
                   x.Name.Contains("search", StringComparison.OrdinalIgnoreCase)))))
            .OrderByDescending(x => HasSemanticRole(x, "windows_search"))
            .ThenByDescending(x => x.Focused)
            .ThenByDescending(x => x.Width * x.Height)
            .FirstOrDefault();

    private static UiElementCandidate? FindVisibleShellTargetCandidate(
        IReadOnlyList<UiElementCandidate> candidates,
        string targetText)
    {
        var normalizedTarget = NormalizeNavigatorText(targetText);
        if (string.IsNullOrWhiteSpace(normalizedTarget)) return null;

        return candidates
            .Where(x => x.Interactable && x.Enabled && !x.Bounds.IsEmpty)
            .Where(x => !x.ControlType.Equals("Edit", StringComparison.OrdinalIgnoreCase) &&
                        !x.ControlType.Equals("ComboBox", StringComparison.OrdinalIgnoreCase))
            .Where(x => x.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase) ||
                        x.ProcessName.Contains("ShellExperienceHost", StringComparison.OrdinalIgnoreCase))
            .Select(x => new { Candidate = x, Score = NavigatorMatchScore(x.Name, normalizedTarget) })
            .Where(x => x.Score >= 600)
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Candidate.Interactable)
            .Select(x => x.Candidate)
            .FirstOrDefault();
    }

    private static UiElementCandidate? FindNavigatorResultCandidate(
        IReadOnlyList<UiElementCandidate> candidates,
        string targetText)
    {
        var normalizedTarget = NormalizeNavigatorText(targetText);
        if (string.IsNullOrWhiteSpace(normalizedTarget)) return null;

        return candidates
            .Where(x => x.Interactable && x.Enabled && !x.Bounds.IsEmpty)
            .Where(x => !x.ControlType.Equals("Edit", StringComparison.OrdinalIgnoreCase) &&
                        !x.ControlType.Equals("ComboBox", StringComparison.OrdinalIgnoreCase))
            .Where(x => x.ProcessName.Contains("SearchHost", StringComparison.OrdinalIgnoreCase) ||
                        x.ProcessName.Contains("StartMenuExperienceHost", StringComparison.OrdinalIgnoreCase) ||
                        x.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase))
            .Select(x => new { Candidate = x, Score = NavigatorMatchScore(x.Name, normalizedTarget) })
            .Where(x => x.Score >= 600)
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Candidate.Interactable)
            .Select(x => x.Candidate)
            .FirstOrDefault();
    }

    private static int NavigatorMatchScore(string? candidateName, string normalizedTarget)
    {
        var name = NormalizeNavigatorText(candidateName);
        if (string.IsNullOrWhiteSpace(name)) return 0;
        if (name.Equals(normalizedTarget, StringComparison.OrdinalIgnoreCase)) return 1000;
        if (name.StartsWith(normalizedTarget, StringComparison.OrdinalIgnoreCase)) return 820;
        if (name.Contains(normalizedTarget, StringComparison.OrdinalIgnoreCase)) return 650;
        return 0;
    }

    private void EnsureNavigatorTransaction(string request, string target)
    {
        if (string.Equals(_outlawNavigatorRequest, request, StringComparison.Ordinal) &&
            string.Equals(_outlawNavigatorTarget, target, StringComparison.OrdinalIgnoreCase)) return;
        _outlawNavigatorRequest = request;
        _outlawNavigatorTarget = target;
        _outlawNavigatorStage = "observe";
        LocalLogService.Write("navigator_start", $"goal=open;target={target}");
    }

    private void SetNavigatorStage(string stage, string detail)
    {
        var before = _outlawNavigatorStage;
        _outlawNavigatorStage = stage;
        LocalLogService.Write("navigator_stage", $"from={before};to={stage};{detail}");
    }

    private static bool IsNavigatorQueryVerified(string expected, string observed, SystemContextSnapshot context)
    {
        if (NavigatorTextEquals(observed, expected)) return true;
        var title = NormalizeNavigatorText(context.ForegroundTitle);
        if (!string.IsNullOrWhiteSpace(title) && title.Contains(NormalizeNavigatorText(expected), StringComparison.OrdinalIgnoreCase))
            return true;
        var url = context.Browser?.Url ?? string.Empty;
        return !string.IsNullOrWhiteSpace(url) &&
               Uri.UnescapeDataString(url).Contains(expected, StringComparison.OrdinalIgnoreCase);
    }

    private static bool NavigatorTextEquals(string? left, string? right)
        => NormalizeNavigatorText(left).Equals(NormalizeNavigatorText(right), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeNavigatorText(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : Regex.Replace(value.Trim(), @"\s+", " ");

    private static bool HasSemanticRole(UiElementCandidate candidate, string role)
        => candidate.AutomationId.Contains($"role:{role}", StringComparison.OrdinalIgnoreCase);

    private string? ResolveGenericOpenTarget(string request)
    {
        var known = ResolveRequestedApplication(request);
        if (known is not null) return known.SearchText;

        var value = NormalizeNavigatorText(request);
        if (string.IsNullOrWhiteSpace(value)) return null;
        var looksLikeOpenGoal =
            value.Contains("開", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("起動", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("見たい", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("表示", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("open ", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("launch ", StringComparison.OrdinalIgnoreCase);
        if (!looksLikeOpenGoal) return null;

        value = Regex.Replace(value, @"^(?:please\s+)?(?:open|launch)\s+", string.Empty, RegexOptions.IgnoreCase);
        value = Regex.Replace(
            value,
            @"(?:を|が)?(?:開いて(?:ください)?|開く|起動して(?:ください)?|起動する|見たい(?:です)?|表示して(?:ください)?)(?:。|！|!|\.)?$",
            string.Empty,
            RegexOptions.IgnoreCase);
        value = value.Trim(' ', '　', '。', '！', '!', '.');
        return value.Length is >= 1 and <= 80 ? value : null;
    }

    private static string? ResolveBeginnerWebSearchText(string request)
    {
        if (request.Contains("youtube", StringComparison.OrdinalIgnoreCase) ||
            request.Contains("ユーチューブ", StringComparison.OrdinalIgnoreCase))
            return "YouTube";

        if (request.Contains("google map", StringComparison.OrdinalIgnoreCase) ||
            request.Contains("googleマップ", StringComparison.OrdinalIgnoreCase) ||
            request.Contains("グーグルマップ", StringComparison.OrdinalIgnoreCase))
            return "Google マップ";

        if (request.Contains("amazon", StringComparison.OrdinalIgnoreCase) ||
            request.Contains("アマゾン", StringComparison.OrdinalIgnoreCase))
            return "Amazon";

        if (request.Contains("楽天", StringComparison.OrdinalIgnoreCase))
            return "楽天";

        if (request.Contains("yahoo", StringComparison.OrdinalIgnoreCase) ||
            request.Contains("ヤフー", StringComparison.OrdinalIgnoreCase))
            return "Yahoo";

        return null;
    }

    private static bool IsBrowserProcessForFastPath(string? processName)
        => processName is not null &&
           (processName.Equals("chrome", StringComparison.OrdinalIgnoreCase) ||
            processName.Equals("msedge", StringComparison.OrdinalIgnoreCase) ||
            processName.Equals("firefox", StringComparison.OrdinalIgnoreCase) ||
            processName.Equals("brave", StringComparison.OrdinalIgnoreCase) ||
            processName.Equals("opera", StringComparison.OrdinalIgnoreCase) ||
            processName.Equals("vivaldi", StringComparison.OrdinalIgnoreCase));
}

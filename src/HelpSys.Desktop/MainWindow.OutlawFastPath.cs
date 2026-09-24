using System.Diagnostics;
using HelpSys.Models;
using HelpSys.Services;

namespace HelpSys;

public partial class MainWindow
{
    private bool TryOutlawBrowserSearchFastPath(
        IReadOnlyList<UiElementCandidate> candidates,
        SystemContextSnapshot context,
        long generation)
    {
        if (!OutlawModePolicy.Enabled ||
            !_sessionState.IsCurrent(generation) ||
            string.IsNullOrWhiteSpace(_activeRequest) ||
            !IsBrowserProcessForFastPath(context.ForegroundProcess))
            return false;

        var searchText = ResolveBeginnerWebSearchText(_activeRequest);
        if (string.IsNullOrWhiteSpace(searchText)) return false;

        // The fast path is only for the first natural-language search on a fresh/home page.
        // Once we already instructed this query to be typed, the next screen must be judged
        // normally (e.g. search results, YouTube home, consent page) instead of typing it again.
        if (_history.Any(h =>
                h.Action.Equals("type_text", StringComparison.OrdinalIgnoreCase) &&
                h.Instruction.Contains(searchText, StringComparison.OrdinalIgnoreCase)))
        {
            LocalLogService.Write("outlaw_local_browser_search_skipped", $"reason=query_already_submitted;query={searchText}");
            return false;
        }

        var searchField = candidates
            .Where(x =>
                x.ProcessId == context.ForegroundProcessId &&
                x.Interactable &&
                x.Enabled &&
                x.KeyboardFocusable &&
                !x.Bounds.IsEmpty &&
                (x.ControlType.Equals("Edit", StringComparison.OrdinalIgnoreCase) ||
                 x.ControlType.Equals("ComboBox", StringComparison.OrdinalIgnoreCase)) &&
                x.Y >= 220 &&
                x.Width >= 220 &&
                (x.Name.Contains("検索", StringComparison.OrdinalIgnoreCase) ||
                 x.Name.Contains("search", StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(x => x.Width * x.Height)
            .FirstOrDefault();

        if (searchField is null) return false;
        if (!_sessionState.TryTransition(generation, GuidanceSessionState.Planning)) return false;

        var decision = searchField.Focused
            ? new GuideDecision(
                "target",
                searchField.Id,
                "type_text",
                $"中央の検索欄に「{searchText}」と入力して、最後に「Enter」と書かれたキーを1回押してください。",
                null,
                "Enter",
                0.995)
            : new GuideDecision(
                "target",
                searchField.Id,
                "left_click",
                $"URLを入力せずに検索します。中央の「{DisplayName(searchField.Name, searchField.ControlType)}」を1回押してください。",
                null,
                null,
                0.995);

        LocalLogService.Write(
            "outlaw_local_browser_search",
            $"target={searchField.Id};focused={searchField.Focused};query={searchText};bounds={searchField.Bounds}");

        ShowStructuredTarget(decision, searchField, candidates, context, generation);
        return true;
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

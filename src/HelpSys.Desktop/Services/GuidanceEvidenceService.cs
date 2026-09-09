using HelpSys.Models;

namespace HelpSys.Services;

public static class GuidanceEvidenceService
{
    public static GuidanceEvidenceSnapshot Build(
        bool screenshotAvailable,
        IReadOnlyList<UiElementCandidate> elements,
        IReadOnlyList<GuideHistoryItem> history,
        SystemContextSnapshot context)
    {
        var focused = elements
            .Where(x => x.Focused)
            .Select(DescribeElement)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToArray();

        var recentTargets = history
            .OrderByDescending(x => x.Step)
            .Select(x => x.TargetName?.Trim() ?? string.Empty)
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToArray();

        var sources = new List<string>(6);
        if (screenshotAvailable) sources.Add("screenshot");
        if (elements.Count > 0) sources.Add("ui-automation");
        if (!string.IsNullOrWhiteSpace(context.ForegroundProcess) || !string.IsNullOrWhiteSpace(context.ForegroundTitle))
            sources.Add("foreground-window");
        if (context.Browser is not null) sources.Add("browser-context");
        if (context.RunningApps.Count > 0 || context.TaskbarVisible) sources.Add("desktop-context");
        if (history.Count > 0) sources.Add("operation-history");

        return new GuidanceEvidenceSnapshot(
            screenshotAvailable,
            elements.Count,
            elements.Count(x => x.Interactable && x.Enabled && !x.Bounds.IsEmpty),
            elements.Count(x => x.Focused),
            focused,
            context.ForegroundProcess ?? string.Empty,
            context.ForegroundTitle ?? string.Empty,
            context.TaskbarVisible,
            context.RunningApps.Count,
            context.Browser?.Domain,
            context.Browser?.Url,
            context.Browser?.AddressFieldFocused == true,
            history.Count,
            recentTargets,
            sources);
    }

    public static string BuildProgressText(GuidanceEvidenceSnapshot evidence)
    {
        var parts = new List<string>();
        if (evidence.ScreenshotAvailable) parts.Add("画面1枚");
        if (evidence.UiElementCount > 0) parts.Add($"操作候補{evidence.UiElementCount}件");
        if (!string.IsNullOrWhiteSpace(evidence.ForegroundProcess)) parts.Add($"前面:{evidence.ForegroundProcess}");
        if (!string.IsNullOrWhiteSpace(evidence.BrowserDomain)) parts.Add($"URL:{evidence.BrowserDomain}");
        if (evidence.HistoryCount > 0) parts.Add($"履歴{evidence.HistoryCount}件");

        return parts.Count == 0
            ? "現在の状態から次の1手を判断しています…"
            : $"{string.Join("＋", parts)}から次の1手を判断しています…";
    }

    private static string DescribeElement(UiElementCandidate element)
    {
        var name = string.IsNullOrWhiteSpace(element.Name) ? element.AutomationId : element.Name;
        if (string.IsNullOrWhiteSpace(name)) name = element.ControlType;
        return string.IsNullOrWhiteSpace(name)
            ? string.Empty
            : $"{name} [{element.ControlType}]";
    }
}

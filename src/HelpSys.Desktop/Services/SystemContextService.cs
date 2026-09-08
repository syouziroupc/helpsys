using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using HelpSys.Models;

namespace HelpSys.Services;

public sealed class SystemContextService
{
    private static readonly HashSet<string> BrowserProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "firefox", "brave", "opera", "vivaldi"
    };

    public SystemContextSnapshot Capture()
    {
        var hwnd = GetForegroundWindow();
        var processId = 0;
        var processName = string.Empty;
        var title = string.Empty;

        if (hwnd != nint.Zero)
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            processId = unchecked((int)pid);
            title = ReadWindowText(hwnd);
            if (processId > 0)
            {
                try { processName = Process.GetProcessById(processId).ProcessName; }
                catch { processName = string.Empty; }
            }
        }

        var running = CaptureRunningWindowProcesses();
        var taskbarVisible = IsTaskbarVisible();
        var browser = BrowserProcesses.Contains(processName)
            ? TryCaptureBrowser(hwnd, processName, title)
            : null;

        return new SystemContextSnapshot(processName, title, processId, taskbarVisible, running, browser);
    }

    private static IReadOnlyList<string> CaptureRunningWindowProcesses()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.MainWindowHandle != nint.Zero && !string.IsNullOrWhiteSpace(process.ProcessName))
                        names.Add(process.ProcessName);
                }
                catch { }
            }
        }
        return names.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(48).ToArray();
    }

    private static BrowserContextSnapshot? TryCaptureBrowser(nint hwnd, string processName, string title)
    {
        if (hwnd == nint.Zero) return new BrowserContextSnapshot(processName, title, null, null, null, false);

        string? bestValue = null;
        var bestScore = int.MinValue;
        var bestFocused = false;

        try
        {
            var root = AutomationElement.FromHandle(hwnd);
            if (root is null) return new BrowserContextSnapshot(processName, title, null, null, null, false);

            var walker = TreeWalker.ControlViewWalker;
            var queue = new Queue<(AutomationElement Element, int Depth)>();
            EnqueueChildren(walker, root, 0, queue);
            var visited = 0;

            while (queue.Count > 0 && visited < 1600)
            {
                var (element, depth) = queue.Dequeue();
                visited++;
                try
                {
                    var current = element.Current;
                    if (!current.IsOffscreen && current.ControlType == ControlType.Edit)
                    {
                        var name = current.Name ?? string.Empty;
                        var automationId = current.AutomationId ?? string.Empty;
                        var className = current.ClassName ?? string.Empty;
                        var hint = $"{name} {automationId} {className}";
                        var score = 0;
                        if (ContainsAddressHint(hint)) score += 80;
                        if (current.HasKeyboardFocus) score += 12;

                        string? value = null;
                        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) && pattern is ValuePattern valuePattern)
                            value = valuePattern.Current.Value;

                        if (LooksLikeLocationValue(value)) score += 70;
                        if (!string.IsNullOrWhiteSpace(value) && score > bestScore)
                        {
                            bestScore = score;
                            bestValue = value.Trim();
                            bestFocused = current.HasKeyboardFocus;
                        }
                    }
                }
                catch (ElementNotAvailableException) { }
                catch (InvalidOperationException) { }

                if (depth < 8) EnqueueChildren(walker, element, depth + 1, queue);
            }
        }
        catch { }

        var normalizedUrl = NormalizeUrl(bestValue);
        string? domain = null;
        bool? https = null;
        if (normalizedUrl is not null && Uri.TryCreate(normalizedUrl, UriKind.Absolute, out var uri) &&
            (uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) || uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)))
        {
            domain = uri.Host.ToLowerInvariant();
            https = uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase);
        }

        return new BrowserContextSnapshot(processName, title, normalizedUrl, domain, https, bestFocused);
    }

    private static bool ContainsAddressHint(string value) =>
        value.Contains("address", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("location", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("omnibox", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("アドレス", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("検索", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("search", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeLocationValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var text = value.Trim();
        if (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return true;
        if (text.StartsWith("chrome://", StringComparison.OrdinalIgnoreCase) || text.StartsWith("edge://", StringComparison.OrdinalIgnoreCase) || text.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) return true;
        if (text.Contains(' ') || text.Length < 4) return false;
        return text.Contains('.') && !text.Contains('\n') && !text.Contains('\r');
    }

    private static string? NormalizeUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        if (text.Length > 900) text = text[..900];
        if (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("chrome://", StringComparison.OrdinalIgnoreCase) || text.StartsWith("edge://", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) return text;
        if (text.Contains(' ') || !text.Contains('.')) return null;
        return $"https://{text}";
    }

    private static bool IsTaskbarVisible()
    {
        var primary = FindWindow("Shell_TrayWnd", null);
        if (primary != nint.Zero && IsWindowVisible(primary)) return true;
        var secondary = FindWindow("Shell_SecondaryTrayWnd", null);
        return secondary != nint.Zero && IsWindowVisible(secondary);
    }

    private static string ReadWindowText(nint hwnd)
    {
        var length = GetWindowTextLength(hwnd);
        if (length <= 0) return string.Empty;
        var builder = new StringBuilder(Math.Min(length + 1, 600));
        GetWindowText(hwnd, builder, builder.Capacity);
        return builder.ToString();
    }

    private static void EnqueueChildren(TreeWalker walker, AutomationElement parent, int depth, Queue<(AutomationElement Element, int Depth)> queue)
    {
        try
        {
            var child = walker.GetFirstChild(parent);
            while (child is not null)
            {
                queue.Enqueue((child, depth));
                child = walker.GetNextSibling(child);
            }
        }
        catch (ElementNotAvailableException) { }
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(nint hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindow(string? className, string? windowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint hWnd);
}

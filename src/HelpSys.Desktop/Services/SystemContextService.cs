using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using HelpSys.Models;

namespace HelpSys.Services;

public sealed class SystemContextService
{
    private const uint EventSystemForeground = 0x0003;
    private const uint WineventOutofcontext = 0x0000;
    private const uint WineventSkipownprocess = 0x0002;
    private const uint StableExternalForegroundDwellMilliseconds = 180;
    private const uint InteractionCandidateMaximumAgeMilliseconds = 2500;
    private static readonly TimeSpan RunningCacheTtl = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan BrowserCacheTtl = TimeSpan.FromMilliseconds(450);
    private readonly int _selfProcessId = Environment.ProcessId;
    private readonly object _cacheGate = new();
    private readonly object _foregroundGate = new();
    private readonly WinEventDelegate _foregroundDelegate;
    private readonly nint _foregroundHook;

    private IReadOnlyList<string> _runningCache = [];
    private DateTime _runningCacheUtc = DateTime.MinValue;
    private BrowserContextSnapshot? _browserCache;
    private nint _browserCacheHwnd;
    private string _browserCacheProcess = string.Empty;
    private string _browserCacheTitle = string.Empty;
    private DateTime _browserCacheUtc = DateTime.MinValue;
    private nint _lastExternalForeground;
    private nint _observedExternalForeground;
    private uint _observedExternalSinceTick;
    private nint _interactionForegroundCandidate;
    private uint _interactionCandidateCapturedTick;
    private int _runningRefreshInFlight;
    private int _browserRefreshInFlight;

    private static readonly HashSet<string> BrowserProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "firefox", "brave", "opera", "vivaldi"
    };

    public SystemContextService()
    {
        _foregroundDelegate = OnForegroundChanged;
        _foregroundHook = SetWinEventHook(
            EventSystemForeground,
            EventSystemForeground,
            nint.Zero,
            _foregroundDelegate,
            0,
            0,
            WineventOutofcontext | WineventSkipownprocess);

        InitializeForegroundTracking(GetForegroundWindow(), CurrentTick());
    }

    ~SystemContextService()
    {
        if (_foregroundHook != nint.Zero) UnhookWinEvent(_foregroundHook);
    }

    public SystemContextSnapshot Capture()
    {
        var hwnd = ResolveEffectiveForegroundWindow();
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

        var running = GetCachedRunningProcesses(processName);
        var taskbarVisible = IsTaskbarActuallyVisible();
        var browser = BrowserProcesses.Contains(processName)
            ? GetCachedBrowser(hwnd, processName, title)
            : null;

        return new SystemContextSnapshot(processName, title, processId, taskbarVisible, running, browser)
        {
            ForegroundWindowHandle = hwnd
        };
    }

    public void CaptureExternalForegroundForAssistantInteraction()
    {
        var foreground = GetForegroundWindow();
        if (!IsUsableExternalWindow(foreground)) return;
        lock (_foregroundGate)
        {
            _interactionForegroundCandidate = foreground;
            _interactionCandidateCapturedTick = CurrentTick();
        }
    }

    public void CommitStableForegroundForAssistantInteraction()
    {
        var nowTick = CurrentTick();
        lock (_foregroundGate)
        {
            if (_interactionForegroundCandidate != nint.Zero &&
                unchecked(nowTick - _interactionCandidateCapturedTick) <= InteractionCandidateMaximumAgeMilliseconds &&
                IsUsableExternalWindow(_interactionForegroundCandidate))
            {
                _lastExternalForeground = _interactionForegroundCandidate;
                _interactionForegroundCandidate = nint.Zero;
                return;
            }

            _interactionForegroundCandidate = nint.Zero;
            PromoteObservedExternalIfStableLocked(nowTick);
        }
    }

    private IReadOnlyList<string> GetCachedRunningProcesses(string foregroundProcess)
    {
        IReadOnlyList<string> cached;
        DateTime capturedUtc;
        lock (_cacheGate)
        {
            cached = _runningCache;
            capturedUtc = _runningCacheUtc;
        }

        if (DateTime.UtcNow - capturedUtc >= RunningCacheTtl) QueueRunningRefresh();

        if (string.IsNullOrWhiteSpace(foregroundProcess) || cached.Contains(foregroundProcess, StringComparer.OrdinalIgnoreCase))
            return cached;

        return cached.Append(foregroundProcess)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .Take(48)
            .ToArray();
    }

    private void QueueRunningRefresh()
    {
        if (Interlocked.CompareExchange(ref _runningRefreshInFlight, 1, 0) != 0) return;
        _ = Task.Run(() =>
        {
            try
            {
                var fresh = CaptureRunningWindowProcesses();
                lock (_cacheGate)
                {
                    _runningCache = fresh;
                    _runningCacheUtc = DateTime.UtcNow;
                }
            }
            catch { }
            finally
            {
                Interlocked.Exchange(ref _runningRefreshInFlight, 0);
            }
        });
    }

    private BrowserContextSnapshot GetCachedBrowser(nint hwnd, string processName, string title)
    {
        BrowserContextSnapshot? cached;
        nint cachedHwnd;
        string cachedProcess;
        string cachedTitle;
        DateTime capturedUtc;
        lock (_cacheGate)
        {
            cached = _browserCache;
            cachedHwnd = _browserCacheHwnd;
            cachedProcess = _browserCacheProcess;
            cachedTitle = _browserCacheTitle;
            capturedUtc = _browserCacheUtc;
        }

        var sameWindow = cached is not null && cachedHwnd == hwnd &&
                         cachedProcess.Equals(processName, StringComparison.OrdinalIgnoreCase) &&
                         cachedTitle.Equals(title, StringComparison.Ordinal);
        if (!sameWindow || DateTime.UtcNow - capturedUtc >= BrowserCacheTtl)
            QueueBrowserRefresh(hwnd, processName, title);

        return sameWindow
            ? cached!
            : new BrowserContextSnapshot(processName, title, null, null, null, false);
    }

    private void QueueBrowserRefresh(nint hwnd, string processName, string title)
    {
        if (hwnd == nint.Zero || Interlocked.CompareExchange(ref _browserRefreshInFlight, 1, 0) != 0) return;
        _ = Task.Run(() =>
        {
            try
            {
                var fresh = TryCaptureBrowser(hwnd, processName, title);
                lock (_cacheGate)
                {
                    _browserCache = fresh;
                    _browserCacheHwnd = hwnd;
                    _browserCacheProcess = processName;
                    _browserCacheTitle = title;
                    _browserCacheUtc = DateTime.UtcNow;
                }
            }
            catch { }
            finally
            {
                Interlocked.Exchange(ref _browserRefreshInFlight, 0);
            }
        });
    }

    private nint ResolveEffectiveForegroundWindow()
    {
        var foreground = GetForegroundWindow();
        if (IsUsableExternalWindow(foreground))
        {
            ObserveForegroundWindow(foreground, CurrentTick());
            return foreground;
        }

        if (!BelongsToSelf(foreground))
            return ResolveVerifiedShellDesktopWindow();

        // HelpSys taking focus must not let a transient notification/terminal/helper window steal
        // the work surface. Use only an external HWND that survived the dwell filter or was captured
        // immediately before the user moved into HelpSys to start guidance.
        lock (_foregroundGate)
        {
            if (IsUsableExternalWindow(_lastExternalForeground)) return _lastExternalForeground;
        }

        return ResolveVerifiedShellDesktopWindow();
    }

    private nint ResolveVerifiedShellDesktopWindow()
    {
        var shell = GetShellWindow();
        if (shell == nint.Zero || BelongsToSelf(shell)) return nint.Zero;
        if (!GetWindowRect(shell, out var rect)) return nint.Zero;
        if (rect.Right - rect.Left < 80 || rect.Bottom - rect.Top < 60) return nint.Zero;

        GetWindowThreadProcessId(shell, out var rawPid);
        var pid = unchecked((int)rawPid);
        if (pid <= 0) return nint.Zero;

        try
        {
            using var process = Process.GetProcessById(pid);
            return process.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase) ? shell : nint.Zero;
        }
        catch
        {
            return nint.Zero;
        }
    }

    private void OnForegroundChanged(
        nint hook,
        uint eventType,
        nint hwnd,
        int idObject,
        int idChild,
        uint eventThread,
        uint eventTime)
    {
        if (eventType == EventSystemForeground) ObserveForegroundWindow(hwnd, eventTime);
    }

    private void InitializeForegroundTracking(nint hwnd, uint observedTick)
    {
        if (!IsUsableExternalWindow(hwnd)) return;
        lock (_foregroundGate)
        {
            _lastExternalForeground = hwnd;
            _observedExternalForeground = hwnd;
            _observedExternalSinceTick = observedTick;
        }
    }

    private void ObserveForegroundWindow(nint hwnd, uint observedTick)
    {
        lock (_foregroundGate)
        {
            if (BelongsToSelf(hwnd))
            {
                PromoteObservedExternalIfStableLocked(observedTick);
                return;
            }

            if (!IsUsableExternalWindow(hwnd)) return;
            if (_observedExternalForeground == hwnd)
            {
                PromoteObservedExternalIfStableLocked(observedTick);
                return;
            }

            PromoteObservedExternalIfStableLocked(observedTick);
            _observedExternalForeground = hwnd;
            _observedExternalSinceTick = observedTick;
        }
    }

    private void PromoteObservedExternalIfStableLocked(uint nowTick)
    {
        if (_observedExternalForeground == nint.Zero) return;
        var elapsed = unchecked(nowTick - _observedExternalSinceTick);
        if (elapsed < StableExternalForegroundDwellMilliseconds) return;
        if (!IsUsableExternalWindow(_observedExternalForeground)) return;
        _lastExternalForeground = _observedExternalForeground;
    }

    private static uint CurrentTick() => unchecked((uint)Environment.TickCount);

    private bool IsUsableExternalWindow(nint hwnd)
    {
        if (hwnd == nint.Zero || BelongsToSelf(hwnd) || !IsWindowVisible(hwnd) || IsIconic(hwnd)) return false;
        if (!GetWindowRect(hwnd, out var rect)) return false;
        return rect.Right - rect.Left >= 80 && rect.Bottom - rect.Top >= 60;
    }

    private bool BelongsToSelf(nint hwnd)
    {
        if (hwnd == nint.Zero) return false;
        GetWindowThreadProcessId(hwnd, out var pid);
        return unchecked((int)pid) == _selfProcessId;
    }

    private IReadOnlyList<string> CaptureRunningWindowProcesses()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.Id == _selfProcessId) continue;
                    if (process.MainWindowHandle != nint.Zero && !string.IsNullOrWhiteSpace(process.ProcessName))
                        names.Add(process.ProcessName);
                }
                catch { }
            }
        }
        return names.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(48).ToArray();
    }

    private static BrowserContextSnapshot TryCaptureBrowser(nint hwnd, string processName, string title)
    {
        if (hwnd == nint.Zero) return new BrowserContextSnapshot(processName, title, null, null, null, false);

        string? bestValue = null;
        var bestScore = int.MinValue;
        var addressFieldFocused = false;

        try
        {
            var root = AutomationElement.FromHandle(hwnd);
            if (root is null) return new BrowserContextSnapshot(processName, title, null, null, null, false);

            var walker = TreeWalker.ControlViewWalker;
            var queue = new Queue<(AutomationElement Element, int Depth)>();
            EnqueueChildren(walker, root, 0, queue);
            var visited = 0;
            var stopwatch = Stopwatch.StartNew();

            while (queue.Count > 0 && visited < 1200 && stopwatch.Elapsed < TimeSpan.FromMilliseconds(700))
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

                        if (!ContainsAddressHint(hint))
                        {
                            if (depth < 8) EnqueueChildren(walker, element, depth + 1, queue);
                            continue;
                        }

                        if (current.HasKeyboardFocus) addressFieldFocused = true;

                        string? value = null;
                        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) && pattern is ValuePattern valuePattern)
                            value = valuePattern.Current.Value;

                        if (LooksLikeLocationValue(value))
                        {
                            var score = 150 + (current.HasKeyboardFocus ? 12 : 0);
                            if (score > bestScore)
                            {
                                bestScore = score;
                                bestValue = value!.Trim();
                            }
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

        return new BrowserContextSnapshot(processName, title, normalizedUrl, domain, https, addressFieldFocused);
    }

    private static bool ContainsAddressHint(string value) =>
        value.Contains("address", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("location", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("omnibox", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("urlbar", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("url bar", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("web address", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("アドレス", StringComparison.OrdinalIgnoreCase);

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

    private static bool IsTaskbarActuallyVisible()
    {
        return TaskbarWindowIsVisible(FindWindow("Shell_TrayWnd", null)) ||
               TaskbarWindowIsVisible(FindWindow("Shell_SecondaryTrayWnd", null));
    }

    private static bool TaskbarWindowIsVisible(nint hwnd)
    {
        if (hwnd == nint.Zero || !IsWindowVisible(hwnd) || !GetWindowRect(hwnd, out var rect)) return false;
        var width = Math.Max(0, rect.Right - rect.Left);
        var height = Math.Max(0, rect.Bottom - rect.Top);
        return width >= 24 && height >= 24;
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

    private delegate void WinEventDelegate(
        nint hook,
        uint eventType,
        nint hwnd,
        int idObject,
        int idChild,
        uint eventThread,
        uint eventTime);

    [StructLayout(LayoutKind.Sequential)]
    private struct RectNative
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern nint SetWinEventHook(
        uint eventMin,
        uint eventMax,
        nint eventHookAssembly,
        WinEventDelegate callback,
        uint processId,
        uint threadId,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(nint hook);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern nint GetShellWindow();

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

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hWnd, out RectNative rect);
}

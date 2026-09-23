using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using HelpSys.Models;

namespace HelpSys.Services;

public sealed class SystemContextService
{
    private const uint EventSystemForeground = 0x0003;
    private const uint WineventOutofcontext = 0x0000;
    private const uint WineventSkipownprocess = 0x0002;
    private const uint StableExternalForegroundDwellMilliseconds = 180;
    private const uint InteractionCandidateMaximumAgeMilliseconds = 2500;
    private const uint AutomaticInteractionHandoffMaximumAgeMilliseconds = 600;
    private static readonly TimeSpan RunningCacheTtl = TimeSpan.FromSeconds(2);
    private readonly int _selfProcessId = Environment.ProcessId;
    private readonly object _cacheGate = new();
    private readonly object _foregroundGate = new();
    private readonly WinEventDelegate _foregroundDelegate;
    private readonly nint _foregroundHook;

    private IReadOnlyList<string> _runningCache = [];
    private DateTime _runningCacheUtc = DateTime.MinValue;
    private nint _lastExternalForeground;
    private nint _observedExternalForeground;
    private uint _observedExternalSinceTick;
    private nint _interactionObservedForeground;
    private int _interactionObservedSamples;
    private nint _interactionForegroundCandidate;
    private uint _interactionCandidateCapturedTick;
    private nint _assistantInteractionForeground;
    private int _runningRefreshInFlight;

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
            ? new BrowserContextSnapshot(processName, title, null, null, null, false)
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
            if (_interactionObservedForeground == foreground)
                _interactionObservedSamples++;
            else
            {
                _interactionObservedForeground = foreground;
                _interactionObservedSamples = 1;
            }

            if (_interactionObservedSamples < 2) return;
            _interactionForegroundCandidate = foreground;
            _interactionCandidateCapturedTick = CurrentTick();
        }
    }

    public void CommitStableForegroundForAssistantInteraction()
    {
        var nowTick = CurrentTick();
        lock (_foregroundGate)
        {
            // A Guide/Enter action happens immediately after HelpSys takes focus. Prefer the
            // external HWND that was most recently observed by the foreground event hook. A
            // sampler candidate can legally be a couple of seconds old and may belong to an
            // earlier transient window, which is not safe enough for an explicit user handoff.
            if (_observedExternalForeground != nint.Zero &&
                unchecked(nowTick - _observedExternalSinceTick) <= AutomaticInteractionHandoffMaximumAgeMilliseconds &&
                IsUsableExternalWindow(_observedExternalForeground))
            {
                _assistantInteractionForeground = _observedExternalForeground;
                _lastExternalForeground = _observedExternalForeground;
                _interactionForegroundCandidate = nint.Zero;
                return;
            }

            if (TryPromoteInteractionCandidateLocked(nowTick, InteractionCandidateMaximumAgeMilliseconds, out var interaction))
            {
                _assistantInteractionForeground = interaction;
                return;
            }

            // Keep the wider age window only as a fallback when the foreground hook did not see a
            // fresh handoff. This preserves slow keyboard/mouse transitions without allowing a
            // stale sampler candidate to override a freshly observed target.
            if (_observedExternalForeground != nint.Zero &&
                unchecked(nowTick - _observedExternalSinceTick) <= InteractionCandidateMaximumAgeMilliseconds &&
                IsUsableExternalWindow(_observedExternalForeground))
            {
                _assistantInteractionForeground = _observedExternalForeground;
                _lastExternalForeground = _observedExternalForeground;
                return;
            }

            PromoteObservedExternalIfStableLocked(nowTick);
            if (IsUsableExternalWindow(_lastExternalForeground))
                _assistantInteractionForeground = _lastExternalForeground;
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

    public SystemContextSnapshot EnrichWithObservedElements(
        SystemContextSnapshot context,
        IReadOnlyList<UiElementCandidate> elements)
    {
        if (!BrowserProcesses.Contains(context.ForegroundProcess)) return context;

        var foregroundElements = elements
            .Where(x =>
                (context.ForegroundProcessId > 0 && x.ProcessId == context.ForegroundProcessId) ||
                x.ProcessName.Equals(context.ForegroundProcess, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var address = foregroundElements
            .Where(x => x.Interactable && x.ControlType is "Edit" or "ComboBox")
            .Where(IsBrowserAddressCandidate)
            .OrderByDescending(x => x.Focused)
            .ThenByDescending(x => !string.IsNullOrWhiteSpace(x.Value))
            .FirstOrDefault();

        var normalizedUrl = NormalizeObservedUrl(address?.Value);
        string? domain = null;
        bool? https = null;
        if (normalizedUrl is not null &&
            Uri.TryCreate(normalizedUrl, UriKind.Absolute, out var uri) &&
            (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
             uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            domain = uri.Host.ToLowerInvariant();
            https = uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        }

        var browser = new BrowserContextSnapshot(
            context.ForegroundProcess,
            context.ForegroundTitle,
            normalizedUrl,
            domain,
            https,
            address?.Focused == true);

        return context with { Browser = browser };
    }

    private static bool IsBrowserAddressCandidate(UiElementCandidate element)
    {
        var role = element.AutomationId ?? string.Empty;
        if (role.Contains("role:browser_address", StringComparison.OrdinalIgnoreCase)) return true;

        var hint = $"{element.Name} {element.AutomationId} {element.ClassName}";
        return hint.Contains("address", StringComparison.OrdinalIgnoreCase) ||
               hint.Contains("location", StringComparison.OrdinalIgnoreCase) ||
               hint.Contains("omnibox", StringComparison.OrdinalIgnoreCase) ||
               hint.Contains("urlbar", StringComparison.OrdinalIgnoreCase) ||
               hint.Contains("url bar", StringComparison.OrdinalIgnoreCase) ||
               hint.Contains("web address", StringComparison.OrdinalIgnoreCase) ||
               hint.Contains("アドレス", StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizeObservedUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        if (text.Length > 900) text = text[..900];

        if (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("chrome://", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("edge://", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
            return text;

        if (text.Contains(' ') || !text.Contains('.')) return null;
        return $"https://{text}";
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

        // Once the user explicitly starts guidance, keep the selected external HWND stable while
        // HelpSys owns foreground. Exact HWND/PID checks later in the pipeline should validate the
        // same work surface instead of re-guessing a different background window on every capture.
        lock (_foregroundGate)
        {
            if (IsUsableExternalWindow(_assistantInteractionForeground))
                return _assistantInteractionForeground;
            _assistantInteractionForeground = nint.Zero;

            if (TryPromoteInteractionCandidateLocked(CurrentTick(), AutomaticInteractionHandoffMaximumAgeMilliseconds, out var interaction))
            {
                _assistantInteractionForeground = interaction;
                return interaction;
            }
            if (IsUsableExternalWindow(_lastExternalForeground)) return _lastExternalForeground;
        }

        return ResolveVerifiedShellDesktopWindow();
    }

    private bool TryPromoteInteractionCandidateLocked(uint nowTick, uint maximumAgeMilliseconds, out nint hwnd)
    {
        hwnd = nint.Zero;
        if (_interactionForegroundCandidate == nint.Zero) return false;
        if (unchecked(nowTick - _interactionCandidateCapturedTick) > maximumAgeMilliseconds) return false;
        if (!IsUsableExternalWindow(_interactionForegroundCandidate)) return false;

        hwnd = _interactionForegroundCandidate;
        _lastExternalForeground = hwnd;
        _interactionForegroundCandidate = nint.Zero;
        return true;
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

            // A genuine new external foreground supersedes any HelpSys-side interaction pin. This
            // preserves live navigation/replanning while preventing HelpSys focus alone from changing
            // the work surface.
            if (_assistantInteractionForeground != nint.Zero && hwnd != _assistantInteractionForeground)
                _assistantInteractionForeground = nint.Zero;

            if (_observedExternalForeground == hwnd)
            {
                PromoteObservedExternalIfStableLocked(observedTick);
                return;
            }

            PromoteObservedExternalIfStableLocked(observedTick);
            _observedExternalForeground = hwnd;
            _observedExternalSinceTick = observedTick;

            // A candidate sampled before this foreground transition is stale by definition.
            // Drop it so an immediate Guide click cannot pin an older unrelated window.
            if (_interactionForegroundCandidate != nint.Zero && _interactionForegroundCandidate != hwnd)
                _interactionForegroundCandidate = nint.Zero;
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

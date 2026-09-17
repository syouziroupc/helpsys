using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Automation;

namespace HelpSys.Stable;

internal sealed class ObservationService
{
    private const int MaxControls = 240;
    private const int MaxVisitedNodes = 800;
    private const int MaxImageDimension = 1600;
    private static readonly TimeSpan UiAutomationTimeout = TimeSpan.FromSeconds(2.5);
    private readonly SemaphoreSlim _uiaGate = new(1, 1);

    public async Task<ScreenObservation> CaptureAsync(CancellationToken cancellationToken)
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == nint.Zero || !NativeMethods.IsWindow(hwnd) || !NativeMethods.IsWindowVisible(hwnd))
            throw new PlannerException("操作中のウィンドウを取得できませんでした。");

        NativeMethods.GetWindowThreadProcessId(hwnd, out var rawPid);
        var pid = checked((int)rawPid);
        if (pid <= 0 || pid == Environment.ProcessId)
            throw new PlannerException("HelpSys以外の操作対象ウィンドウを前面に出してください。");

        if (!NativeMethods.GetWindowRect(hwnd, out var rect) || rect.Width < 40 || rect.Height < 40)
            throw new PlannerException("操作中のウィンドウ領域を取得できませんでした。");

        var title = NativeMethods.GetTitle(hwnd);
        var processName = GetProcessName(pid);

        UiScanResult scan;
        try
        {
            scan = await RunUiScanAsync(hwnd, processName, rect, cancellationToken);
        }
        catch (TimeoutException ex)
        {
            throw new PlannerException("Windowsの画面構造取得が応答しなかったため、画像を送信せず中止しました。", ex);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PlannerException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new PlannerException("Windowsの画面構造を安全に取得できなかったため、画像を送信せず中止しました。", ex);
        }

        SafetyGate.EnsureSafeToCapture(processName, title, scan.Controls);
        EnsureSameWindow(hwnd, pid);

        var image = CaptureWindow(rect);
        EnsureSameWindow(hwnd, pid);

        return new ScreenObservation(
            hwnd,
            pid,
            processName,
            title,
            scan.BrowserDomain,
            rect.Left,
            rect.Top,
            rect.Width,
            rect.Height,
            image,
            scan.Controls);
    }

    public bool IsStillCurrent(ScreenObservation observation)
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd != observation.WindowHandle || hwnd == nint.Zero) return false;
        NativeMethods.GetWindowThreadProcessId(hwnd, out var rawPid);
        return checked((int)rawPid) == observation.ProcessId;
    }

    private async Task<UiScanResult> RunUiScanAsync(
        nint hwnd,
        string processName,
        NativeMethods.Rect windowRect,
        CancellationToken cancellationToken)
    {
        if (!await _uiaGate.WaitAsync(0, cancellationToken))
            throw new PlannerException("前回のWindows画面構造取得がまだ終了していないため、新しい取得を重ねません。");

        Task<UiScanResult>? scanTask = null;
        var releaseHere = true;
        try
        {
            scanTask = Task.Run(() => ScanUi(hwnd, processName, windowRect), CancellationToken.None);
            return await scanTask.WaitAsync(UiAutomationTimeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            if (scanTask is not null && !scanTask.IsCompleted)
            {
                releaseHere = false;
                ReleaseGateWhenComplete(scanTask);
            }
            throw;
        }
        catch (OperationCanceledException)
        {
            if (scanTask is not null && !scanTask.IsCompleted)
            {
                releaseHere = false;
                ReleaseGateWhenComplete(scanTask);
            }
            throw;
        }
        finally
        {
            if (releaseHere) _uiaGate.Release();
        }
    }

    private void ReleaseGateWhenComplete(Task scanTask)
    {
        _ = scanTask.ContinueWith(
            _ => _uiaGate.Release(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static UiScanResult ScanUi(nint hwnd, string processName, NativeMethods.Rect windowRect)
    {
        var root = AutomationElement.FromHandle(hwnd)
                   ?? throw new InvalidOperationException("UI Automation root unavailable.");

        var walker = TreeWalker.ControlViewWalker;
        var queue = new Queue<AutomationElement>();
        var first = walker.GetFirstChild(root);
        if (first is not null) queue.Enqueue(first);

        var controls = new List<UiControlSnapshot>(MaxControls);
        string? browserDomain = null;
        var visited = 0;

        while (queue.Count > 0 && controls.Count < MaxControls && visited < MaxVisitedNodes)
        {
            var item = queue.Dequeue();
            visited++;

            try
            {
                var child = walker.GetFirstChild(item);
                var siblingBudget = 0;
                while (child is not null && queue.Count < MaxVisitedNodes && siblingBudget < MaxVisitedNodes)
                {
                    queue.Enqueue(child);
                    siblingBudget++;
                    child = walker.GetNextSibling(child);
                }

                var current = item.Current;
                var bounds = current.BoundingRectangle;
                if (bounds.IsEmpty || bounds.Width < 2 || bounds.Height < 2) continue;

                var clippedLeft = Math.Max(bounds.Left, windowRect.Left);
                var clippedTop = Math.Max(bounds.Top, windowRect.Top);
                var clippedRight = Math.Min(bounds.Right, windowRect.Right);
                var clippedBottom = Math.Min(bounds.Bottom, windowRect.Bottom);
                if (clippedRight <= clippedLeft || clippedBottom <= clippedTop) continue;

                var name = (current.Name ?? string.Empty).Trim();
                var type = current.ControlType?.ProgrammaticName ?? string.Empty;
                var id = $"u{controls.Count + 1}";

                controls.Add(new UiControlSnapshot(
                    id,
                    name.Length <= 180 ? name : name[..180],
                    type.Length <= 80 ? type : type[..80],
                    current.IsEnabled,
                    current.HasKeyboardFocus,
                    current.IsKeyboardFocusable,
                    current.IsPassword,
                    Normalize(clippedLeft - windowRect.Left, windowRect.Width),
                    Normalize(clippedTop - windowRect.Top, windowRect.Height),
                    Normalize(clippedRight - clippedLeft, windowRect.Width),
                    Normalize(clippedBottom - clippedTop, windowRect.Height)));

                if (browserDomain is null && IsBrowser(processName) && LooksLikeAddressBar(name, type))
                    browserDomain = TryReadDomain(item);
            }
            catch (ElementNotAvailableException)
            {
                // Normal UI churn: skip only the vanished node.
            }
            catch (InvalidOperationException)
            {
                // Unsupported property/pattern on one node must not abort the whole observation.
            }
        }

        return new UiScanResult(controls, browserDomain);
    }

    private static string CaptureWindow(NativeMethods.Rect rect)
    {
        using var source = new Bitmap(rect.Width, rect.Height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(source))
        {
            graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0, new Size(rect.Width, rect.Height), CopyPixelOperation.SourceCopy);
        }

        Bitmap output = source;
        var scale = Math.Min(1d, MaxImageDimension / (double)Math.Max(source.Width, source.Height));
        if (scale < 1d)
        {
            var width = Math.Max(1, (int)Math.Round(source.Width * scale));
            var height = Math.Max(1, (int)Math.Round(source.Height * scale));
            output = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            using var g = Graphics.FromImage(output);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(source, 0, 0, width, height);
        }

        try
        {
            using var stream = new MemoryStream();
            var encoder = ImageCodecInfo.GetImageEncoders().First(x => x.FormatID == ImageFormat.Jpeg.Guid);
            using var quality = new EncoderParameters(1);
            quality.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 82L);
            output.Save(stream, encoder, quality);
            return "data:image/jpeg;base64," + Convert.ToBase64String(stream.ToArray());
        }
        finally
        {
            if (!ReferenceEquals(output, source)) output.Dispose();
        }
    }

    private static void EnsureSameWindow(nint expectedHwnd, int expectedPid)
    {
        var current = NativeMethods.GetForegroundWindow();
        if (current != expectedHwnd)
            throw new ObservationChangedException("画面取得中に前面ウィンドウが切り替わりました。");

        NativeMethods.GetWindowThreadProcessId(current, out var rawPid);
        if (checked((int)rawPid) != expectedPid)
            throw new ObservationChangedException("画面取得中に操作対象アプリが切り替わりました。");
    }

    private static string GetProcessName(int pid)
    {
        try { return Process.GetProcessById(pid).ProcessName; }
        catch { return string.Empty; }
    }

    private static double Normalize(double value, double total)
        => total <= 0 ? 0 : Math.Clamp(value / total * 1000d, 0d, 1000d);

    private static bool IsBrowser(string processName)
        => processName.Equals("chrome", StringComparison.OrdinalIgnoreCase) ||
           processName.Equals("msedge", StringComparison.OrdinalIgnoreCase) ||
           processName.Equals("firefox", StringComparison.OrdinalIgnoreCase) ||
           processName.Equals("brave", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeAddressBar(string name, string controlType)
    {
        if (!controlType.Contains("Edit", StringComparison.OrdinalIgnoreCase)) return false;
        return name.Contains("address", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("search bar", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("アドレス", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("検索", StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryReadDomain(AutomationElement element)
    {
        try
        {
            if (!element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) || pattern is not ValuePattern valuePattern)
                return null;
            var raw = valuePattern.Current.Value?.Trim();
            if (string.IsNullOrWhiteSpace(raw)) return null;
            if (!raw.Contains("://", StringComparison.Ordinal)) raw = "https://" + raw;
            return Uri.TryCreate(raw, UriKind.Absolute, out var uri) &&
                   (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
                ? uri.IdnHost.ToLowerInvariant()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private sealed record UiScanResult(IReadOnlyList<UiControlSnapshot> Controls, string? BrowserDomain);
}

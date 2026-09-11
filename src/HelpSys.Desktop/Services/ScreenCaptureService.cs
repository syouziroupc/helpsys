using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HelpSys.Models;

namespace HelpSys.Services;

public sealed class ScreenCaptureService
{
    private const uint Srccopy = 0x00CC0020;
    private const uint CaptureBlt = 0x40000000;
    private const uint Blackness = 0x00000042;
    private const uint GwHwndNext = 2;
    private const uint GwHwndPrev = 3;
    private const uint MonitorDefaultToNearest = 0x00000002;
    private const int MaxImageWidth = 1280;
    private const int MaxImageHeight = 720;
    private static readonly HashSet<string> FullMonitorShellProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "SearchHost", "StartMenuExperienceHost", "ShellExperienceHost"
    };

    // These patterns are deliberately high-confidence. The goal is to remove obvious PII/secret
    // strings from pixels before egress without blanket-redacting every useful label on screen.
    private static readonly Regex VisibleEmailRegex = new(
        @"(?<![\w.+-])[\w.+-]+@[\w.-]+\.[A-Za-z]{2,}(?![\w.-])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex VisibleJapanesePhoneRegex = new(
        @"(?<!\d)(?:(?:0[5789]0[- ]?\d{4}[- ]?\d{4})|(?:0\d{1,4}[- ]\d{1,4}[- ]\d{3,4})|(?:\+81[- ]?[1-9]\d{0,4}[- ]?\d{1,4}[- ]?\d{3,4}))(?!\d)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex VisiblePostalCodeRegex = new(
        @"(?<!\d)〒?\s*\d{3}-\d{4}(?!\d)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex VisibleLabeledSecretRegex = new(
        @"(?i)\b(password|passwd|passcode|otp|totp|2fa|mfa|api[ _-]?key|client[ _-]?secret|access[ _-]?token|refresh[ _-]?token|session[ _-]?token|backup[ _-]?code|recovery[ _-]?code)\b\s*[:=]\s*([^\s,;]{3,})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex VisibleBearerRegex = new(
        @"(?i)\bbearer\s+[A-Za-z0-9._~+/=-]{8,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex VisibleJwtRegex = new(
        @"\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex VisibleApiKeyRegex = new(
        @"(?i)\b(?:sk-[A-Za-z0-9_-]{16,}|gh[pousr]_[A-Za-z0-9]{20,}|AIza[A-Za-z0-9_-]{20,}|AKIA[0-9A-Z]{16})\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex VisiblePrivateKeyRegex = new(
        @"-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex VisibleCardNumberRegex = new(
        @"(?<!\d)(?:\d[ -]?){13,19}(?!\d)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex VisibleSensitiveUrlRegex = new(
        @"https?://[^\s<>""']*[?#][^\s<>""']+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private readonly int _selfProcessId = Environment.ProcessId;

    public Task<ScreenCaptureFrame> CaptureAsync(IReadOnlyList<Rect> redactions, CancellationToken cancellationToken = default)
        => Task.Run(() => Capture(redactions, cancellationToken), cancellationToken);

    public ScreenCaptureFrame Capture(IReadOnlyList<Rect> redactions) => Capture(redactions, CancellationToken.None);

    private ScreenCaptureFrame Capture(IReadOnlyList<Rect> redactions, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var captureArea = ResolveCaptureArea();
        if (captureArea.Width <= 0 || captureArea.Height <= 0) throw new InvalidOperationException("画面サイズを取得できませんでした。");

        // The ranked guidance candidate list is finite, so it cannot be the privacy boundary.
        // Independently inspect UIA trees intersecting only the area that will leave the process.
        // Every visible input control plus high-confidence visible PII/secret text is redacted before
        // egress. Windows layered above the selected work surface are also redacted so unrelated
        // notifications, overlays and popups cannot hitchhike into the outbound screenshot.
        // If any bounded privacy scan cannot finish, fail closed instead of sending a partial image.
        var sensitiveRedactionsBefore = CaptureSensitiveInputBounds(captureArea, cancellationToken);
        var occluderRedactionsBefore = CaptureOccluderBounds(captureArea, cancellationToken);

        var desktopDc = GetDC(IntPtr.Zero);
        if (desktopDc == IntPtr.Zero) throw new InvalidOperationException("画面キャプチャーを開始できませんでした。");

        var memoryDc = IntPtr.Zero;
        var bitmap = IntPtr.Zero;
        var previous = IntPtr.Zero;
        BitmapSource? source = null;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            memoryDc = CreateCompatibleDC(desktopDc);
            bitmap = CreateCompatibleBitmap(desktopDc, captureArea.Width, captureArea.Height);
            if (memoryDc == IntPtr.Zero || bitmap == IntPtr.Zero) throw new InvalidOperationException("画面キャプチャー用バッファーを作成できませんでした。");

            previous = SelectObject(memoryDc, bitmap);
            if (!BitBlt(memoryDc, 0, 0, captureArea.Width, captureArea.Height, desktopDc, captureArea.X, captureArea.Y, Srccopy | CaptureBlt))
                throw new InvalidOperationException("画面を取得できませんでした。");

            cancellationToken.ThrowIfCancellationRequested();
            // Scan again after BitBlt to cover sensitive content or an occluding window that appeared
            // during capture. A race must make the result more redacted, never less.
            var sensitiveRedactionsAfter = CaptureSensitiveInputBounds(captureArea, cancellationToken);
            var occluderRedactionsAfter = CaptureOccluderBounds(captureArea, cancellationToken);
            var allRedactions = redactions
                .Concat(sensitiveRedactionsBefore)
                .Concat(sensitiveRedactionsAfter)
                .Concat(occluderRedactionsBefore)
                .Concat(occluderRedactionsAfter)
                .Where(x => !x.IsEmpty)
                .Distinct()
                .ToArray();

            foreach (var rect in allRedactions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                RedactOrThrow(memoryDc, rect, captureArea.X, captureArea.Y, captureArea.Width, captureArea.Height);
            }

            source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
        }
        finally
        {
            if (previous != IntPtr.Zero && memoryDc != IntPtr.Zero) SelectObject(memoryDc, previous);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (memoryDc != IntPtr.Zero) DeleteDC(memoryDc);
            ReleaseDC(IntPtr.Zero, desktopDc);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (source is null) throw new InvalidOperationException("安全な画面画像を作成できませんでした。");
        var output = ScaleToLimit(source);
        using var stream = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(output));
        encoder.Save(stream);
        var dataUri = "data:image/png;base64," + Convert.ToBase64String(stream.ToArray());

        return new ScreenCaptureFrame(dataUri, captureArea.X, captureArea.Y, captureArea.Width, captureArea.Height, output.PixelWidth, output.PixelHeight);
    }

    private CaptureArea ResolveCaptureArea()
    {
        // HelpSys itself is topmost while the user asks for guidance. Walk behind it to the first
        // normal visible application. For normal applications, capture only that window clipped to
        // its monitor. This is data minimization: unrelated desktop/background windows never enter
        // the image. Shell surfaces need the whole monitor because the desktop, Start/search and
        // taskbar are themselves the operation surface.
        var hwnd = GetForegroundWindow();
        if (BelongsToSelf(hwnd))
        {
            var cursor = hwnd;
            for (var i = 0; i < 96; i++)
            {
                cursor = GetWindow(cursor, GwHwndNext);
                if (cursor == IntPtr.Zero) break;
                if (!IsWindowVisible(cursor) || IsIconic(cursor) || BelongsToSelf(cursor)) continue;
                if (!GetWindowRect(cursor, out var rect)) continue;
                if (rect.Right - rect.Left < 80 || rect.Bottom - rect.Top < 60) continue;
                hwnd = cursor;
                break;
            }
        }

        var targetProcessId = 0;
        if (hwnd != IntPtr.Zero && !BelongsToSelf(hwnd))
        {
            GetWindowThreadProcessId(hwnd, out var rawTargetPid);
            targetProcessId = unchecked((int)rawTargetPid);
        }
        var shellSurface = hwnd != IntPtr.Zero && targetProcessId > 0 && IsShellSurface(hwnd);

        IntPtr monitor = IntPtr.Zero;
        if (hwnd != IntPtr.Zero && !BelongsToSelf(hwnd)) monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero && GetCursorPos(out var cursorPoint)) monitor = MonitorFromPoint(cursorPoint, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
            throw new InvalidOperationException("操作中のモニターを特定できないため、画面画像を送信しません。");

        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info))
            throw new InvalidOperationException("操作中のモニター領域を取得できないため、画面画像を送信しません。");

        var monitorArea = new CaptureArea(
            info.Monitor.Left,
            info.Monitor.Top,
            info.Monitor.Right - info.Monitor.Left,
            info.Monitor.Bottom - info.Monitor.Top,
            hwnd,
            targetProcessId,
            shellSurface);
        if (monitorArea.Width <= 0 || monitorArea.Height <= 0)
            throw new InvalidOperationException("操作中のモニター領域が不正なため、画面画像を送信しません。");

        if (hwnd == IntPtr.Zero || BelongsToSelf(hwnd) || shellSurface || !GetWindowRect(hwnd, out var windowRect))
            return monitorArea;

        var left = Math.Max(windowRect.Left, info.Monitor.Left);
        var top = Math.Max(windowRect.Top, info.Monitor.Top);
        var right = Math.Min(windowRect.Right, info.Monitor.Right);
        var bottom = Math.Min(windowRect.Bottom, info.Monitor.Bottom);
        var width = right - left;
        var height = bottom - top;
        if (width < 80 || height < 60) return monitorArea;

        return new CaptureArea(left, top, width, height, hwnd, targetProcessId, false);
    }

    private bool BelongsToSelf(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        GetWindowThreadProcessId(hwnd, out var pid);
        return unchecked((int)pid) == _selfProcessId;
    }

    private static bool IsShellSurface(IntPtr hwnd)
    {
        try
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0) return true;
            using var process = Process.GetProcessById(unchecked((int)pid));
            return FullMonitorShellProcesses.Contains(process.ProcessName);
        }
        catch
        {
            // Unknown ownership must not shrink the image around a possibly wrong window.
            // The Privacy Gate still controls whether the resulting image can leave the process.
            return true;
        }
    }

    private static bool IsRelatedShellProcess(int processId)
    {
        if (processId <= 0) return false;
        try
        {
            using var process = Process.GetProcessById(processId);
            return FullMonitorShellProcesses.Contains(process.ProcessName);
        }
        catch
        {
            return false;
        }
    }

    private IReadOnlyList<Rect> CaptureOccluderBounds(CaptureArea captureArea, CancellationToken cancellationToken)
    {
        if (captureArea.TargetWindow == IntPtr.Zero || captureArea.TargetProcessId <= 0) return [];

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var captureRect = new Rect(captureArea.X, captureArea.Y, captureArea.Width, captureArea.Height);
            var result = new List<Rect>();
            var visited = new HashSet<IntPtr>();
            var hwnd = GetWindow(captureArea.TargetWindow, GwHwndPrev);

            const int maxWindows = 256;
            var count = 0;
            while (hwnd != IntPtr.Zero)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++count > maxWindows || !visited.Add(hwnd))
                    throw new InvalidOperationException("前面ウィンドウの安全確認を規定範囲内で完了できませんでした。");

                if (IsWindowVisible(hwnd) && !IsIconic(hwnd))
                {
                    GetWindowThreadProcessId(hwnd, out var rawPid);
                    var processId = unchecked((int)rawPid);
                    var relatedShell = captureArea.ShellSurface && IsRelatedShellProcess(processId);
                    if (!relatedShell && GetWindowRect(hwnd, out var windowRect))
                    {
                        var rect = new Rect(
                            windowRect.Left,
                            windowRect.Top,
                            Math.Max(0, windowRect.Right - windowRect.Left),
                            Math.Max(0, windowRect.Bottom - windowRect.Top));
                        if (!rect.IsEmpty && rect.Width >= 2 && rect.Height >= 2 && captureRect.IntersectsWith(rect))
                            result.Add(Rect.Intersect(captureRect, rect));
                    }
                }

                hwnd = GetWindow(hwnd, GwHwndPrev);
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("前面に重なった別画面を安全に除外できないため、画面画像は送信しません。", ex);
        }
    }

    private IReadOnlyList<Rect> CaptureSensitiveInputBounds(CaptureArea captureArea, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var captureRect = new Rect(captureArea.X, captureArea.Y, captureArea.Width, captureArea.Height);
            var walker = TreeWalker.ControlViewWalker;
            var queue = new Queue<AutomationElement>();
            var roots = AutomationElement.RootElement.FindAll(TreeScope.Children, System.Windows.Automation.Condition.TrueCondition);

            foreach (AutomationElement root in roots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var current = root.Current;
                    if (current.ProcessId == _selfProcessId || current.IsOffscreen) continue;
                    var bounds = current.BoundingRectangle;
                    if (!bounds.IsEmpty && captureRect.IntersectsWith(bounds)) queue.Enqueue(root);
                }
                catch (ElementNotAvailableException) { }
            }

            const int maxVisited = 12000;
            var stopwatch = Stopwatch.StartNew();
            var visited = 0;
            var result = new List<Rect>();

            while (queue.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++visited > maxVisited || stopwatch.Elapsed > TimeSpan.FromSeconds(1.8))
                    throw new InvalidOperationException("画面の安全確認を規定範囲内で完了できませんでした。");

                var element = queue.Dequeue();
                try
                {
                    var current = element.Current;
                    if (current.ProcessId != _selfProcessId && !current.IsOffscreen)
                    {
                        var bounds = current.BoundingRectangle;
                        if ((ShouldRedactInput(current) || ShouldRedactVisibleSensitiveText(current.Name)) &&
                            !bounds.IsEmpty && captureRect.IntersectsWith(bounds))
                            result.Add(bounds);
                    }

                    var child = walker.GetFirstChild(element);
                    while (child is not null)
                    {
                        queue.Enqueue(child);
                        child = walker.GetNextSibling(child);
                    }
                }
                catch (ElementNotAvailableException) { }
                catch (InvalidOperationException) { }
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("入力欄や表示済み秘密情報を安全に確認できないため、画面画像は送信しません。", ex);
        }
    }

    private static bool ShouldRedactInput(AutomationElement.AutomationElementInformation current)
    {
        if (current.IsPassword) return true;
        return current.ControlType == ControlType.Edit || current.ControlType == ControlType.ComboBox;
    }

    private static bool ShouldRedactVisibleSensitiveText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var value = text.Length <= 800 ? text : text[..800];
        if (VisibleEmailRegex.IsMatch(value) || VisibleJapanesePhoneRegex.IsMatch(value) || VisiblePostalCodeRegex.IsMatch(value) ||
            VisibleLabeledSecretRegex.IsMatch(value) || VisibleBearerRegex.IsMatch(value) || VisibleJwtRegex.IsMatch(value) ||
            VisibleApiKeyRegex.IsMatch(value) || VisiblePrivateKeyRegex.IsMatch(value) || VisibleSensitiveUrlRegex.IsMatch(value))
            return true;

        foreach (Match match in VisibleCardNumberRegex.Matches(value))
        {
            var digits = new string(match.Value.Where(char.IsDigit).ToArray());
            if (digits.Length is >= 13 and <= 19 && PassesLuhn(digits)) return true;
        }
        return false;
    }

    private static bool PassesLuhn(string digits)
    {
        var sum = 0;
        var alternate = false;
        for (var i = digits.Length - 1; i >= 0; i--)
        {
            var n = digits[i] - '0';
            if (alternate)
            {
                n *= 2;
                if (n > 9) n -= 9;
            }
            sum += n;
            alternate = !alternate;
        }
        return sum % 10 == 0;
    }

    private static BitmapSource ScaleToLimit(BitmapSource source)
    {
        var scale = Math.Min(1d, Math.Min(MaxImageWidth / (double)source.PixelWidth, MaxImageHeight / (double)source.PixelHeight));
        if (scale >= 0.999) return source;

        var resized = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        resized.Freeze();
        return resized;
    }

    private static void RedactOrThrow(IntPtr dc, Rect rect, int screenX, int screenY, int screenWidth, int screenHeight)
    {
        if (rect.IsEmpty) return;

        const int guard = 4;
        var left = Math.Clamp((int)Math.Floor(rect.Left - screenX) - guard, 0, screenWidth);
        var top = Math.Clamp((int)Math.Floor(rect.Top - screenY) - guard, 0, screenHeight);
        var right = Math.Clamp((int)Math.Ceiling(rect.Right - screenX) + guard, 0, screenWidth);
        var bottom = Math.Clamp((int)Math.Ceiling(rect.Bottom - screenY) + guard, 0, screenHeight);
        if (right <= left || bottom <= top) return;

        if (!PatBlt(dc, left, top, right - left, bottom - top, Blackness))
            throw new InvalidOperationException("秘密情報の領域を安全に黒塗りできないため、画面画像は送信しません。");
    }

    private readonly record struct CaptureArea(
        int X,
        int Y,
        int Width,
        int Height,
        IntPtr TargetWindow,
        int TargetProcessId,
        bool ShellSurface);

    [StructLayout(LayoutKind.Sequential)]
    private struct PointNative { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RectNative { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public RectNative Monitor;
        public RectNative Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr dc);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RectNative rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out PointNative point);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(PointNative point, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr dc);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(IntPtr destDc, int x, int y, int width, int height, IntPtr srcDc, int srcX, int srcY, uint rop);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PatBlt(IntPtr dc, int x, int y, int width, int height, uint rop);
}

using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HelpSys.Models;

namespace HelpSys;

// MainWindow-facing capture boundary. It keeps the exact PID/HWND binding and whole-window
// occluder protection, but does not erase every ordinary Edit/ComboBox. Sensitive fields are
// identified locally from password semantics and the value patterns below; only the resulting
// rectangles are blacked before any image can leave the machine.
public sealed class ScreenCaptureService
{
    private const uint Srccopy = 0x00CC0020;
    private const uint CaptureBlt = 0x40000000;
    private const uint Blackness = 0x00000042;
    private const uint GwHwndPrev = 3;
    private const uint MonitorDefaultToNearest = 0x00000002;
    private const int MaxImageWidth = 1280;
    private const int MaxImageHeight = 720;

    private static readonly HashSet<string> FullMonitorShellProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "SearchHost", "StartMenuExperienceHost", "ShellExperienceHost"
    };

    private static readonly string[] SensitiveInputTerms =
    [
        "password", "passwd", "passcode", "パスワード", "暗証番号", "pin", "otp", "totp", "2fa", "mfa",
        "one-time", "verification code", "security code", "認証コード", "確認コード", "ワンタイム",
        "api key", "apikey", "apiキー", "client secret", "secret key", "access token", "refresh token",
        "session token", "bearer token", "秘密鍵", "private key", "backup code", "recovery code",
        "cvv", "cvc", "card number", "カード番号"
    ];

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

    public Task<ScreenCaptureFrame> CaptureAsync(
        IReadOnlyList<Rect> redactions,
        int expectedProcessId,
        nint expectedWindowHandle,
        CancellationToken cancellationToken = default)
        => Task.Run(() => Capture(redactions, expectedProcessId, expectedWindowHandle, cancellationToken), cancellationToken);

    public Task<ScreenCaptureFrame> CaptureAsync(
        IReadOnlyList<Rect> redactions,
        int expectedProcessId,
        CancellationToken cancellationToken = default)
        => Task.FromException<ScreenCaptureFrame>(new InvalidOperationException(
            "検証済みウィンドウ識別子が無いため、画面画像を取得・送信しません。"));

    public Task<ScreenCaptureFrame> CaptureAsync(IReadOnlyList<Rect> redactions, CancellationToken cancellationToken = default)
        => Task.FromException<ScreenCaptureFrame>(new InvalidOperationException(
            "検証済みウィンドウ識別子が無いため、画面画像を取得・送信しません。"));

    public ScreenCaptureFrame Capture(IReadOnlyList<Rect> redactions)
        => throw new InvalidOperationException("検証済みウィンドウ識別子が無いため、画面画像を取得・送信しません。");

    private ScreenCaptureFrame Capture(
        IReadOnlyList<Rect> redactions,
        int expectedProcessId,
        nint expectedWindowHandle,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var captureArea = ResolveCaptureArea(expectedProcessId, expectedWindowHandle);

        var sensitiveBefore = CaptureSensitiveBounds(captureArea, cancellationToken);
        var occludersBefore = CaptureOccluderBounds(captureArea, cancellationToken);

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
            if (memoryDc == IntPtr.Zero || bitmap == IntPtr.Zero)
                throw new InvalidOperationException("画面キャプチャー用バッファーを作成できませんでした。");

            previous = SelectObject(memoryDc, bitmap);
            if (!BitBlt(memoryDc, 0, 0, captureArea.Width, captureArea.Height, desktopDc, captureArea.X, captureArea.Y, Srccopy | CaptureBlt))
                throw new InvalidOperationException("画面を取得できませんでした。");

            var sensitiveAfter = CaptureSensitiveBounds(captureArea, cancellationToken);
            var occludersAfter = CaptureOccluderBounds(captureArea, cancellationToken);
            var all = redactions
                .Concat(sensitiveBefore)
                .Concat(sensitiveAfter)
                .Concat(occludersBefore)
                .Concat(occludersAfter)
                .Where(x => !x.IsEmpty)
                .Distinct()
                .ToArray();

            foreach (var rect in all)
            {
                cancellationToken.ThrowIfCancellationRequested();
                RedactOrThrow(memoryDc, rect, captureArea.X, captureArea.Y, captureArea.Width, captureArea.Height);
            }

            source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
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

        return new ScreenCaptureFrame(
            dataUri,
            captureArea.X,
            captureArea.Y,
            captureArea.Width,
            captureArea.Height,
            output.PixelWidth,
            output.PixelHeight);
    }

    private CaptureArea ResolveCaptureArea(int expectedProcessId, nint expectedWindowHandle)
    {
        if (expectedProcessId <= 0 || expectedWindowHandle == nint.Zero)
            throw new InvalidOperationException("操作対象のウィンドウを安全に特定できないため、画面画像を送信しません。");

        var hwnd = (IntPtr)expectedWindowHandle;
        if (BelongsToSelf(hwnd) || IsIconic(hwnd))
            throw new InvalidOperationException("検証済み操作対象ウィンドウが現在利用できないため、画面画像を送信しません。");

        GetWindowThreadProcessId(hwnd, out var rawPid);
        var targetPid = unchecked((int)rawPid);
        if (targetPid <= 0 || targetPid != expectedProcessId)
            throw new InvalidOperationException("操作対象プロセスとウィンドウの対応を確認できないため、画面画像を送信しません。");

        var shellSurface = IsShellSurface(hwnd);
        if (!shellSurface && !IsWindowVisible(hwnd))
            throw new InvalidOperationException("検証済み操作対象ウィンドウが現在利用できないため、画面画像を送信しません。");

        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
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
            targetPid,
            shellSurface);
        if (monitorArea.Width <= 0 || monitorArea.Height <= 0)
            throw new InvalidOperationException("操作中のモニター領域が不正なため、画面画像を送信しません。");

        if (shellSurface) return monitorArea;

        if (!GetWindowRect(hwnd, out var windowRect))
            throw new InvalidOperationException("操作対象ウィンドウの領域を取得できないため、画面画像を送信しません。");

        var left = Math.Max(windowRect.Left, info.Monitor.Left);
        var top = Math.Max(windowRect.Top, info.Monitor.Top);
        var right = Math.Min(windowRect.Right, info.Monitor.Right);
        var bottom = Math.Min(windowRect.Bottom, info.Monitor.Bottom);
        var width = right - left;
        var height = bottom - top;
        if (width < 80 || height < 60)
            throw new InvalidOperationException("操作対象ウィンドウを安全な範囲で取得できないため、画面画像を送信しません。");

        return new CaptureArea(left, top, width, height, hwnd, targetPid, false);
    }

    private IReadOnlyList<Rect> CaptureOccluderBounds(CaptureArea captureArea, CancellationToken cancellationToken)
    {
        var captureRect = new Rect(captureArea.X, captureArea.Y, captureArea.Width, captureArea.Height);
        var result = new List<Rect>();
        var visited = new HashSet<IntPtr>();
        var hwnd = GetWindow(captureArea.TargetWindow, GwHwndPrev);

        for (var count = 0; hwnd != IntPtr.Zero; count++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (count >= 320 || !visited.Add(hwnd))
                throw new InvalidOperationException("前面ウィンドウの安全確認を規定範囲内で完了できませんでした。");

            if (IsWindowVisible(hwnd) && !IsIconic(hwnd))
            {
                GetWindowThreadProcessId(hwnd, out var rawPid);
                var pid = unchecked((int)rawPid);
                var relatedShell = captureArea.ShellSurface && IsRelatedShellProcess(pid);
                if (!relatedShell && GetWindowRect(hwnd, out var rectNative))
                {
                    var rect = new Rect(
                        rectNative.Left,
                        rectNative.Top,
                        Math.Max(0, rectNative.Right - rectNative.Left),
                        Math.Max(0, rectNative.Bottom - rectNative.Top));
                    if (!rect.IsEmpty && rect.Width >= 2 && rect.Height >= 2 && captureRect.IntersectsWith(rect))
                        result.Add(Rect.Intersect(captureRect, rect));
                }
            }

            hwnd = GetWindow(hwnd, GwHwndPrev);
        }

        return result;
    }

    private IReadOnlyList<Rect> CaptureSensitiveBounds(CaptureArea captureArea, CancellationToken cancellationToken)
    {
        try
        {
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
                    var inspect = current.ProcessId == captureArea.TargetProcessId ||
                                  (captureArea.ShellSurface && IsRelatedShellProcess(current.ProcessId));
                    if (!inspect || current.ProcessId == _selfProcessId || current.IsOffscreen) continue;
                    var bounds = current.BoundingRectangle;
                    if (!bounds.IsEmpty && captureRect.IntersectsWith(bounds)) queue.Enqueue(root);
                }
                catch (ElementNotAvailableException) { }
            }

            var result = new List<Rect>();
            var visited = 0;
            var stopwatch = Stopwatch.StartNew();
            while (queue.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++visited > 18000 || stopwatch.Elapsed > TimeSpan.FromSeconds(2.8))
                    throw new InvalidOperationException("画面の秘密情報確認を規定範囲内で完了できませんでした。");

                var element = queue.Dequeue();
                try
                {
                    var current = element.Current;
                    if (current.ProcessId != _selfProcessId && !current.IsOffscreen)
                    {
                        var bounds = current.BoundingRectangle;
                        if (!bounds.IsEmpty && captureRect.IntersectsWith(bounds) && ShouldRedactElement(element, current))
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
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            throw new InvalidOperationException("秘密情報を安全に確認できないため、画面画像は送信しません。", ex);
        }
    }

    private static bool ShouldRedactElement(
        AutomationElement element,
        AutomationElement.AutomationElementInformation current)
    {
        if (current.IsPassword) return true;
        if (ShouldRedactVisibleSensitiveText(current.Name)) return true;

        if (current.ControlType is not null &&
            (current.ControlType == ControlType.Edit || current.ControlType == ControlType.ComboBox))
        {
            var hint = $"{current.Name} {current.AutomationId} {current.ClassName}";
            if (ContainsSensitiveInputHint(hint)) return true;

            try
            {
                if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) && pattern is ValuePattern valuePattern)
                    return ShouldRedactVisibleSensitiveText(valuePattern.Current.Value);
            }
            catch (ElementNotAvailableException) { return true; }
            catch (InvalidOperationException) { return true; }
        }

        return false;
    }

    private static bool ContainsSensitiveInputHint(string value)
        => SensitiveInputTerms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));

    private static bool ShouldRedactVisibleSensitiveText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var value = text.Length <= 1000 ? text : text[..1000];
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
        for (var index = digits.Length - 1; index >= 0; index--)
        {
            var number = digits[index] - '0';
            if (alternate)
            {
                number *= 2;
                if (number > 9) number -= 9;
            }
            sum += number;
            alternate = !alternate;
        }
        return sum % 10 == 0;
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
            if (pid == 0) return false;
            using var process = Process.GetProcessById(unchecked((int)pid));
            return FullMonitorShellProcesses.Contains(process.ProcessName);
        }
        catch { return false; }
    }

    private static bool IsRelatedShellProcess(int processId)
    {
        if (processId <= 0) return false;
        try
        {
            using var process = Process.GetProcessById(processId);
            return FullMonitorShellProcesses.Contains(process.ProcessName);
        }
        catch { return false; }
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
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
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

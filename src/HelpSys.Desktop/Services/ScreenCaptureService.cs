using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
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
    private const uint MonitorDefaultToNearest = 0x00000002;
    private const int MaxImageWidth = 1280;
    private const int MaxImageHeight = 720;
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
        // Independently inspect the UIA trees of windows that intersect only the monitor being captured.
        // Password controls and every visible Edit/ComboBox are treated as private input. We intentionally
        // do not depend on ValuePattern readability: an unreadable input can still contain visible private
        // text. If this bounded scan cannot finish, fail closed instead of sending a partial image.
        var sensitiveRedactionsBefore = CaptureSensitiveInputBounds(captureArea, cancellationToken);

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
            var sensitiveRedactionsAfter = CaptureSensitiveInputBounds(captureArea, cancellationToken);
            var allRedactions = redactions
                .Concat(sensitiveRedactionsBefore)
                .Concat(sensitiveRedactionsAfter)
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
        // normal visible application and capture only that monitor. This avoids transmitting an
        // unrelated second monitor and preserves more pixels for the screen the user is operating.
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

        IntPtr monitor = IntPtr.Zero;
        if (hwnd != IntPtr.Zero && !BelongsToSelf(hwnd)) monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero && GetCursorPos(out var cursorPoint)) monitor = MonitorFromPoint(cursorPoint, MonitorDefaultToNearest);

        if (monitor != IntPtr.Zero)
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfo(monitor, ref info))
            {
                var width = info.Monitor.Right - info.Monitor.Left;
                var height = info.Monitor.Bottom - info.Monitor.Top;
                if (width > 0 && height > 0) return new CaptureArea(info.Monitor.Left, info.Monitor.Top, width, height);
            }
        }

        throw new InvalidOperationException("操作中のモニターを特定できないため、複数画面をまとめて送信せずVision案内を停止します。");
    }

    private bool BelongsToSelf(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        GetWindowThreadProcessId(hwnd, out var pid);
        return unchecked((int)pid) == _selfProcessId;
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
                    throw new InvalidOperationException("入力欄の安全確認を規定範囲内で完了できませんでした。");

                var element = queue.Dequeue();
                try
                {
                    var current = element.Current;
                    if (current.ProcessId != _selfProcessId && !current.IsOffscreen)
                    {
                        var bounds = current.BoundingRectangle;
                        if (ShouldRedactInput(current) && !bounds.IsEmpty && captureRect.IntersectsWith(bounds)) result.Add(bounds);
                    }

                    var child = walker.GetFirstChild(element);
                    while (child is not null)
                    {
                        queue.Enqueue(child);
                        child = walker.GetNextSibling(child);
                    }
                }
                catch (ElementNotAvailableException) { }
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("入力欄を安全に確認できないため、画面画像は送信しません。", ex);
        }
    }

    private static bool ShouldRedactInput(AutomationElement.AutomationElementInformation current) =>
        current.IsPassword || current.ControlType == ControlType.Edit || current.ControlType == ControlType.ComboBox;

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
            throw new InvalidOperationException("入力欄を安全に黒塗りできないため、画面画像は送信しません。");
    }

    private readonly record struct CaptureArea(int X, int Y, int Width, int Height);

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

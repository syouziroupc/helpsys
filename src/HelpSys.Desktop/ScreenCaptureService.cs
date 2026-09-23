using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HelpSys.Models;
using HelpSys.Services;

namespace HelpSys;

// MainWindow-facing capture boundary.
// UI Automation is deliberately absent here. Redaction rectangles must already belong to the
// immutable observation snapshot used for planning. This keeps screenshot capture bounded to Win32
// window identity/Z-order checks and removes a second, potentially blocking UIA tree walk.
public sealed class ScreenCaptureService
{
    private const uint Srccopy = 0x00CC0020;
    private const uint CaptureBlt = 0x40000000;
    private const uint Blackness = 0x00000042;
    private const uint GwHwndPrev = 3;
    private const uint MonitorDefaultToNearest = 0x00000002;
    private const int MaxImageWidth = 2560;
    private const int MaxImageHeight = 1440;

    private static readonly HashSet<string> FullMonitorShellProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "SearchHost", "StartMenuExperienceHost", "ShellExperienceHost"
    };

    private readonly int _selfProcessId = Environment.ProcessId;

    public Task<ScreenCaptureFrame> CaptureAsync(
        IReadOnlyList<Rect> redactions,
        int expectedProcessId,
        nint expectedWindowHandle,
        CancellationToken cancellationToken = default)
        => PerformanceTrace.MeasureAsync(
            "screenshot.capture",
            () => Task.Run(
                () => Capture(redactions, expectedProcessId, expectedWindowHandle, cancellationToken),
                cancellationToken));

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

        // Outlaw keeps the real pixels. Normal builds redact windows that occlude the verified target.
        var occludersBefore = OutlawModePolicy.Enabled
            ? Array.Empty<Rect>()
            : CaptureOccluderBounds(captureArea, cancellationToken);

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

            var occludersAfter = OutlawModePolicy.Enabled
                ? Array.Empty<Rect>()
                : CaptureOccluderBounds(captureArea, cancellationToken);
            var all = OutlawModePolicy.Enabled
                ? Array.Empty<Rect>()
                : redactions
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

        if (OutlawModePolicy.Enabled || shellSurface) return monitorArea;

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
            throw new InvalidOperationException("秘密情報の領域を安全に黒塗りできないため、画面画像を送信しません。");
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

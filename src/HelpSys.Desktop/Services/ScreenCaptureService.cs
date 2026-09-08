using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HelpSys.Models;

namespace HelpSys.Services;

public sealed class ScreenCaptureService
{
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;
    private const uint Srccopy = 0x00CC0020;
    private const uint CaptureBlt = 0x40000000;
    private const uint Blackness = 0x00000042;
    private const int MaxImageWidth = 1280;
    private const int MaxImageHeight = 720;

    public ScreenCaptureFrame Capture(IReadOnlyList<Rect> redactions)
    {
        var screenX = GetSystemMetrics(SmXVirtualScreen);
        var screenY = GetSystemMetrics(SmYVirtualScreen);
        var screenWidth = GetSystemMetrics(SmCxVirtualScreen);
        var screenHeight = GetSystemMetrics(SmCyVirtualScreen);
        if (screenWidth <= 0 || screenHeight <= 0) throw new InvalidOperationException("画面サイズを取得できませんでした。");

        var desktopDc = GetDC(IntPtr.Zero);
        if (desktopDc == IntPtr.Zero) throw new InvalidOperationException("画面キャプチャーを開始できませんでした。");

        var memoryDc = IntPtr.Zero;
        var bitmap = IntPtr.Zero;
        var previous = IntPtr.Zero;
        BitmapSource? source = null;

        try
        {
            memoryDc = CreateCompatibleDC(desktopDc);
            bitmap = CreateCompatibleBitmap(desktopDc, screenWidth, screenHeight);
            if (memoryDc == IntPtr.Zero || bitmap == IntPtr.Zero) throw new InvalidOperationException("画面キャプチャー用バッファーを作成できませんでした。");

            previous = SelectObject(memoryDc, bitmap);
            if (!BitBlt(memoryDc, 0, 0, screenWidth, screenHeight, desktopDc, screenX, screenY, Srccopy | CaptureBlt))
                throw new InvalidOperationException("画面を取得できませんでした。");

            foreach (var rect in redactions) Redact(memoryDc, rect, screenX, screenY, screenWidth, screenHeight);

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

        var output = ScaleToLimit(source!);
        using var stream = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(output));
        encoder.Save(stream);
        var dataUri = "data:image/png;base64," + Convert.ToBase64String(stream.ToArray());

        return new ScreenCaptureFrame(dataUri, screenX, screenY, screenWidth, screenHeight, output.PixelWidth, output.PixelHeight);
    }

    private static BitmapSource ScaleToLimit(BitmapSource source)
    {
        var scale = Math.Min(1d, Math.Min(MaxImageWidth / (double)source.PixelWidth, MaxImageHeight / (double)source.PixelHeight));
        if (scale >= 0.999) return source;

        var resized = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        resized.Freeze();
        return resized;
    }

    private static void Redact(IntPtr dc, Rect rect, int screenX, int screenY, int screenWidth, int screenHeight)
    {
        if (rect.IsEmpty) return;
        var left = Math.Clamp((int)Math.Floor(rect.Left - screenX), 0, screenWidth);
        var top = Math.Clamp((int)Math.Floor(rect.Top - screenY), 0, screenHeight);
        var right = Math.Clamp((int)Math.Ceiling(rect.Right - screenX), 0, screenWidth);
        var bottom = Math.Clamp((int)Math.Ceiling(rect.Bottom - screenY), 0, screenHeight);
        if (right <= left || bottom <= top) return;
        PatBlt(dc, left, top, right - left, bottom - top, Blackness);
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr dc);

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

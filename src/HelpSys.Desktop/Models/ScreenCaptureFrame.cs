using System.Windows;

namespace HelpSys.Models;

public sealed record ScreenCaptureFrame(
    string ImageDataUri,
    int ScreenX,
    int ScreenY,
    int ScreenWidth,
    int ScreenHeight,
    int ImageWidth,
    int ImageHeight)
{
    public Rect MapNormalizedBounds(double x, double y, double width, double height)
    {
        var leftN = Math.Clamp(x, 0, 1000);
        var topN = Math.Clamp(y, 0, 1000);
        var rightN = Math.Clamp(x + width, leftN, 1000);
        var bottomN = Math.Clamp(y + height, topN, 1000);

        return new Rect(
            ScreenX + leftN / 1000d * ScreenWidth,
            ScreenY + topN / 1000d * ScreenHeight,
            Math.Max(10, (rightN - leftN) / 1000d * ScreenWidth),
            Math.Max(10, (bottomN - topN) / 1000d * ScreenHeight));
    }
}

using System.Globalization;
using System.Text.Json;

namespace HelpSys.Stable;

internal static class UiProbeHost
{
    public const string Switch = "--uia-probe";

    public static bool IsInvocation(IReadOnlyList<string> args)
        => args.Count > 0 && args[0].Equals(Switch, StringComparison.Ordinal);

    public static int Run(IReadOnlyList<string> args)
    {
        if (args.Count != 8) return 64;

        try
        {
            var hwndValue = long.Parse(args[1], CultureInfo.InvariantCulture);
            var processName = args[2];
            var rect = new NativeMethods.Rect
            {
                Left = int.Parse(args[3], CultureInfo.InvariantCulture),
                Top = int.Parse(args[4], CultureInfo.InvariantCulture),
                Right = int.Parse(args[5], CultureInfo.InvariantCulture),
                Bottom = int.Parse(args[6], CultureInfo.InvariantCulture)
            };
            var outputPath = Path.GetFullPath(args[7]);
            var result = ObservationService.ScanUi(new nint(hwndValue), processName, rect);
            var json = JsonSerializer.Serialize(result);
            File.WriteAllText(outputPath, json);
            return 0;
        }
        catch (Exception ex)
        {
            try
            {
                if (args.Count > 7)
                {
                    var outputPath = Path.GetFullPath(args[7]);
                    File.WriteAllText(outputPath + ".error", ex.GetType().Name);
                }
            }
            catch { }
            return 70;
        }
    }
}

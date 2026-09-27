using System.IO;
using System.Text.Json;

namespace HelpSys.Stable;

internal static class SafetyAudit
{
    private static readonly object Gate = new();

    public static void Record(string eventName, string category, PlanResult? plan)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "HelpSysStable");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "safety-audit.jsonl");
            var row = JsonSerializer.Serialize(new
            {
                timestamp = DateTimeOffset.Now,
                eventName,
                category,
                status = plan?.Status,
                action = plan?.Action,
                targetPresent = !string.IsNullOrWhiteSpace(plan?.TargetId)
            });

            lock (Gate)
                File.AppendAllText(path, row + Environment.NewLine);
        }
        catch
        {
        }
    }
}

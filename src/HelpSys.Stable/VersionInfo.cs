using System.Reflection;

namespace HelpSys.Stable;

internal static class VersionInfo
{
    public const string Version = "3.0.1";
    public const string ProductName = "HelpSys Stable 3.0.1 — Gemini Edition";
    public const string ModelName = "Gemini 3.8 Flash";
    public static readonly System.Version SemanticVersion = new(3, 0, 1);
    public static readonly string BuildId =
        Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(x => x.Key == "HelpSysBuildId")?.Value?.Trim()
        ?? "dev";
}

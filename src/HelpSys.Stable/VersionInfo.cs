using System.Reflection;

namespace HelpSys.Stable;

internal static class VersionInfo
{
    public const string Version = "A3.0001";
    public const int ReleaseNumber = 1;
    public const string ProductName = "HelpSys A3.0001 — Standard";
    public const string ModelName = "Gemini 3.8 Flash";
    public static readonly string BuildId =
        Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(x => x.Key == "HelpSysBuildId")?.Value?.Trim()
        ?? "dev";
}

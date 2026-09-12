namespace HelpSys.Services;

/// <summary>
/// Diagnostic mode may expose non-content performance/state diagnostics when explicitly enabled.
/// Raw screen persistence is permanently disabled in the unified build.
/// This policy itself never writes files.
/// </summary>
public sealed class DiagnosticModePolicy
{
    public DiagnosticModePolicy()
    {
        Enabled = string.Equals(
            Environment.GetEnvironmentVariable("HELPSYS_DIAGNOSTIC_MODE"),
            "1",
            StringComparison.Ordinal);
        RawScreenPersistenceAllowed = false;
    }

    public bool Enabled { get; }
    public bool RawScreenPersistenceAllowed { get; }
}

namespace HelpSys.Services;

/// <summary>
/// Diagnostic mode is disabled by default. Raw screen persistence requires an explicit second
/// opt-in and is permanently disabled in the Safe build. This policy alone never writes files.
/// </summary>
public sealed class DiagnosticModePolicy
{
    public DiagnosticModePolicy()
    {
#if HELPSYS_SAFE_BUILD
        Enabled = false;
        RawScreenPersistenceAllowed = false;
#else
        Enabled = string.Equals(
            Environment.GetEnvironmentVariable("HELPSYS_DIAGNOSTIC_MODE"),
            "1",
            StringComparison.Ordinal);
        RawScreenPersistenceAllowed = Enabled && string.Equals(
            Environment.GetEnvironmentVariable("HELPSYS_DIAGNOSTIC_RAW_SCREEN"),
            "I_UNDERSTAND_RAW_SCREEN_DATA",
            StringComparison.Ordinal);
#endif
    }

    public bool Enabled { get; }
    public bool RawScreenPersistenceAllowed { get; }
}

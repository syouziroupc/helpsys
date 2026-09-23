namespace HelpSys.Services;

public static class OutlawModePolicy
{
#if HELPSYS_OUTLAW_BUILD
    public const bool Enabled = true;
#else
    public const bool Enabled = false;
#endif
}

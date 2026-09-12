using System.Reflection;

namespace HelpSys.Services;

public enum TelemetryAppKind
{
    Unknown,
    WindowsShell,
    Browser,
    Office,
    FileManager,
    Settings,
    Other
}

public enum TelemetryTaskKind
{
    Unknown,
    Navigation,
    Search,
    Settings,
    FileOperationGuidance,
    FormGuidance,
    Education,
    Other
}

public enum TelemetryErrorCode
{
    None,
    Network,
    ServiceUnavailable,
    Rejected,
    InvalidResponse,
    ContextChanged,
    PrivacyBlocked,
    CaptureUnavailable,
    Unknown
}

public enum TelemetrySupportStage
{
    Idle,
    Capturing,
    Planning,
    Presenting,
    AwaitingUserAction,
    PrivacyMode,
    Completed,
    Failed
}

/// <summary>
/// Fixed long-term telemetry schema. Deliberately contains no free-form user text,
/// screenshot, OCR, URL, document/mail body, input value, cookie, token or credential field.
/// </summary>
public sealed record PrivacySafeTelemetryEvent(
    Guid SessionId,
    string HelpSysVersion,
    TelemetryAppKind AppKind,
    TelemetryTaskKind TaskKind,
    bool Success,
    int ResponseTimeMs,
    TelemetryErrorCode ErrorCode,
    TelemetrySupportStage SupportStage);

public interface IPrivacySafeTelemetrySink
{
    void Record(PrivacySafeTelemetryEvent item);
}

public sealed class NullPrivacySafeTelemetrySink : IPrivacySafeTelemetrySink
{
    public void Record(PrivacySafeTelemetryEvent item) { }
}

/// <summary>
/// Telemetry is opt-in and metadata-only. The default sink is a no-op, so merely constructing
/// this service never creates a local file or sends data to a server.
/// </summary>
public sealed class PrivacySafeTelemetry
{
    private readonly IPrivacySafeTelemetrySink _sink;

    public PrivacySafeTelemetry(IPrivacySafeTelemetrySink? sink = null)
    {
        _sink = sink ?? new NullPrivacySafeTelemetrySink();
        Enabled = string.Equals(
            Environment.GetEnvironmentVariable("HELPSYS_TELEMETRY"),
            "1",
            StringComparison.Ordinal);
    }

    public bool Enabled { get; }

    public void Record(
        Guid sessionId,
        TelemetryAppKind appKind,
        TelemetryTaskKind taskKind,
        bool success,
        TimeSpan responseTime,
        TelemetryErrorCode errorCode,
        TelemetrySupportStage supportStage)
    {
        if (!Enabled) return;
        if (sessionId == Guid.Empty) return;

        var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "0.0.0.0";
        var elapsedMs = (int)Math.Clamp(responseTime.TotalMilliseconds, 0, int.MaxValue);
        _sink.Record(new PrivacySafeTelemetryEvent(
            sessionId,
            version,
            appKind,
            taskKind,
            success,
            elapsedMs,
            errorCode,
            supportStage));
    }
}

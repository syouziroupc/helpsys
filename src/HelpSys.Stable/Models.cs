namespace HelpSys.Stable;

public enum AppPhase
{
    Idle,
    Capturing,
    Planning,
    Validating,
    ShowingResult,
    Listening,
    Updating
}

public sealed record UiControlSnapshot(
    string Id,
    string Name,
    string AutomationId,
    string ClassName,
    string ControlType,
    bool Enabled,
    bool Focused,
    bool KeyboardFocusable,
    bool Password,
    double X,
    double Y,
    double Width,
    double Height);

public sealed record ScreenObservation(
    nint WindowHandle,
    int ProcessId,
    string ProcessName,
    string WindowTitle,
    string? BrowserDomain,
    int X,
    int Y,
    int Width,
    int Height,
    string ImageDataUri,
    string LocalComparisonImageDataUri,
    IReadOnlyList<UiControlSnapshot> Controls);

public sealed record PlanResult(
    string Status,
    string Action,
    string Instruction,
    string? Question,
    string? TargetId,
    string? Key,
    double Confidence,
    double X,
    double Y,
    double Width,
    double Height);

public sealed record UpdateInfo(
    System.Version Version,
    string BuildId,
    Uri ZipUrl,
    Uri Sha256Url,
    long SizeBytes);

public sealed class PrivacyBlockedException(string message) : Exception(message);
public sealed class ObservationChangedException(string message) : Exception(message);
public sealed class PlannerException(string message, Exception? inner = null) : Exception(message, inner);

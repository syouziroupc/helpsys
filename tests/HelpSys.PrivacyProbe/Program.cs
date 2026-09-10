using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using HelpSys.Models;
using HelpSys.Services;
using HelpSys.Shared;

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void AssertThrows<T>(Action action, string message) where T : Exception
{
    try
    {
        action();
    }
    catch (T)
    {
        return;
    }
    throw new InvalidOperationException(message);
}

var expectSafeProfile = args.Contains("--expect-safe-profile", StringComparer.OrdinalIgnoreCase);
var gate = new PrivacyGate();
var safeContext = new SystemContextSnapshot(
    "explorer",
    "Windows Settings",
    1234,
    true,
    ["explorer"],
    null);

Assert(gate.EvaluateState(safeContext, []).Classification == PrivacyClassification.Safe,
    "General Windows state must remain usable.");

var password = new UiElementCandidate(
    "pw", "Password", "passwordBox", "PasswordBox", "Edit", "browser",
    true, true, true, true, true,
    10, 10, 200, 32, 1234);
Assert(gate.EvaluateState(safeContext, [password]).Classification == PrivacyClassification.Blocked,
    "Password control must hard-block cloud screen analysis.");

var otpContext = safeContext with { ForegroundProcess = "msedge", ForegroundTitle = "Enter verification code", ForegroundProcessId = 2233 };
Assert(gate.EvaluateState(otpContext, []).Classification == PrivacyClassification.Blocked,
    "OTP/verification-code state must hard-block cloud screen analysis.");

var storageContext = safeContext with { ForegroundProcess = "chrome", ForegroundTitle = "DevTools - Application - Cookies", ForegroundProcessId = 3233 };
Assert(gate.EvaluateState(storageContext, []).Classification == PrivacyClassification.Blocked,
    "Cookie/Storage state must hard-block cloud screen analysis.");

var rawApiKeyContext = safeContext with
{
    ForegroundProcess = "notepad",
    ForegroundTitle = "sk-ABCDEFGHIJKLMNOPQRSTUV",
    ForegroundProcessId = 4233
};
Assert(gate.EvaluateState(rawApiKeyContext, []).Classification == PrivacyClassification.Blocked,
    "A raw high-confidence API-key pattern must hard-block cloud screen analysis even without an API-key label.");

var rawBearerContext = safeContext with
{
    ForegroundProcess = "notepad",
    ForegroundTitle = "Bearer AbCdEfGhIjKlMnOpQrStUvWxYz012345",
    ForegroundProcessId = 5233
};
Assert(gate.EvaluateState(rawBearerContext, []).Classification == PrivacyClassification.Blocked,
    "A raw bearer-token pattern must hard-block cloud screen analysis.");

var rawJwtContext = safeContext with
{
    ForegroundProcess = "notepad",
    ForegroundTitle = "eyJAAAAAAAAAAAA.BBBBBBBBBBBB.CCCCCCCCCCCC",
    ForegroundProcessId = 6233
};
Assert(gate.EvaluateState(rawJwtContext, []).Classification == PrivacyClassification.Blocked,
    "A raw JWT-like token must hard-block cloud screen analysis.");

var rawCardContext = safeContext with
{
    ForegroundProcess = "notepad",
    ForegroundTitle = "4242 4242 4242 4242",
    ForegroundProcessId = 7233
};
Assert(gate.EvaluateState(rawCardContext, []).Classification == PrivacyClassification.Blocked,
    "A valid card-number pattern must hard-block cloud screen analysis.");

var unknownContext = safeContext with { ForegroundProcess = "", ForegroundProcessId = 0 };
Assert(gate.EvaluateState(unknownContext, []).Classification == PrivacyClassification.Unknown,
    "UNKNOWN must not silently become SAFE.");

gate.SetManualPause(true);
Assert(gate.EvaluateState(safeContext, []).Classification == PrivacyClassification.ManualPause,
    "Manual screen-analysis pause must prevent egress.");
gate.SetManualPause(false);
Assert(gate.EvaluateState(safeContext, []).Classification == PrivacyClassification.Safe,
    "Manual pause must be resumable on a safe screen.");

var browserContext = new SystemContextSnapshot(
    "msedge",
    "Example page",
    2233,
    true,
    ["msedge"],
    new BrowserContextSnapshot(
        "msedge",
        "Example page",
        "https://example.com/account/reset?auth=SECRET_QUERY_VALUE#private",
        "example.com",
        true,
        false));
var editWithSecret = new UiElementCandidate(
    "edit", "Search", "searchBox", "TextBox", "Edit", "msedge",
    true, true, true, false, false,
    10, 10, 200, 32, 2233,
    "USER_TYPED_SECRET_VALUE");
var frame = new ScreenCaptureFrame("data:image/png;base64,AA==", 0, 0, 100, 100, 100, 100);
var secretRichRequest = "contact alice@example.com phone 090-1234-5678 postal 123-4567 password=hunter2 key sk-ABCDEFGHIJKLMNOPQRSTUV card 4242 4242 4242 4242 open https://example.com/reset?token=REQUEST_SECRET#fragment";
var approval = gate.ApproveQuality(secretRichRequest, frame, [editWithSecret], [], browserContext, false, null);
Assert(approval.CanSend, "Benign browser context should remain usable after outbound minimization.");
var outboundJson = JsonSerializer.Serialize(approval.Body, new JsonSerializerOptions
{
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
});
Assert(!outboundJson.Contains("SECRET_QUERY_VALUE", StringComparison.Ordinal),
    "Full URL query/fragment data must never enter outbound cloud evidence.");
Assert(!outboundJson.Contains("/account/reset", StringComparison.Ordinal),
    "Full browser URL path must never enter outbound cloud evidence.");
Assert(outboundJson.Contains("example.com", StringComparison.Ordinal),
    "Browser domain should remain available for useful cloud guidance.");
Assert(!outboundJson.Contains("USER_TYPED_SECRET_VALUE", StringComparison.Ordinal),
    "Raw UI input values must never enter outbound cloud payloads.");
Assert(!outboundJson.Contains("alice@example.com", StringComparison.Ordinal) && outboundJson.Contains("<email>", StringComparison.Ordinal),
    "Email addresses typed into the HelpSys request must be redacted before egress.");
Assert(!outboundJson.Contains("090-1234-5678", StringComparison.Ordinal) && outboundJson.Contains("<phone>", StringComparison.Ordinal),
    "Japanese phone numbers typed into the HelpSys request must be redacted before egress.");
Assert(!outboundJson.Contains("123-4567", StringComparison.Ordinal) && outboundJson.Contains("<postal-code>", StringComparison.Ordinal),
    "Japanese postal codes typed into the HelpSys request must be redacted before egress.");
Assert(!outboundJson.Contains("hunter2", StringComparison.Ordinal) && outboundJson.Contains("<redacted-secret>", StringComparison.Ordinal),
    "Labeled secrets typed into the HelpSys request must be redacted before egress.");
Assert(!outboundJson.Contains("sk-ABCDEFGHIJKLMNOPQRSTUV", StringComparison.Ordinal) && outboundJson.Contains("<redacted-api-key>", StringComparison.Ordinal),
    "Raw API keys typed into the HelpSys request must be redacted before egress.");
Assert(!outboundJson.Contains("4242 4242 4242 4242", StringComparison.Ordinal) && outboundJson.Contains("<redacted-card>", StringComparison.Ordinal),
    "Valid card numbers typed into the HelpSys request must be redacted before egress.");
Assert(!outboundJson.Contains("REQUEST_SECRET", StringComparison.Ordinal) && !outboundJson.Contains("/reset", StringComparison.Ordinal),
    "Request URLs must be reduced to origin before egress.");

AssertThrows<InvalidOperationException>(
    () => { using var _ = new CloudAiAdapter("http://example.com"); },
    "External plaintext HTTP must be rejected.");
using (var loopback = new CloudAiAdapter("http://127.0.0.1:8787")) { }

var oldNormalApiBase = Environment.GetEnvironmentVariable("HELPSYS_API_BASE");
var oldSafeApiBase = Environment.GetEnvironmentVariable("HELPSYS_SAFE_API_BASE");
var oldSafeApiKey = Environment.GetEnvironmentVariable("HELPSYS_SAFE_API_KEY");
try
{
    Environment.SetEnvironmentVariable("HELPSYS_API_BASE", "http://127.0.0.1:8787");
    Environment.SetEnvironmentVariable("HELPSYS_SAFE_API_BASE", null);
    Environment.SetEnvironmentVariable("HELPSYS_SAFE_API_KEY", null);
    using var defaultAdapter = new CloudAiAdapter();
    if (expectSafeProfile)
        Assert(!defaultAdapter.IsConfigured,
            "Safe build must fail closed when HELPSYS_SAFE_API_BASE is absent, even if HELPSYS_API_BASE is set.");
    else
        Assert(defaultAdapter.IsConfigured,
            "Normal build should retain its configured/default cloud endpoint behavior.");
}
finally
{
    Environment.SetEnvironmentVariable("HELPSYS_API_BASE", oldNormalApiBase);
    Environment.SetEnvironmentVariable("HELPSYS_SAFE_API_BASE", oldSafeApiBase);
    Environment.SetEnvironmentVariable("HELPSYS_SAFE_API_KEY", oldSafeApiKey);
}

var oldDiagnostic = Environment.GetEnvironmentVariable("HELPSYS_DIAGNOSTIC_MODE");
var oldRawDiagnostic = Environment.GetEnvironmentVariable("HELPSYS_DIAGNOSTIC_RAW_SCREEN");
try
{
    Environment.SetEnvironmentVariable("HELPSYS_DIAGNOSTIC_MODE", null);
    Environment.SetEnvironmentVariable("HELPSYS_DIAGNOSTIC_RAW_SCREEN", null);
    var diagnosticsOff = new DiagnosticModePolicy();
    Assert(!diagnosticsOff.Enabled && !diagnosticsOff.RawScreenPersistenceAllowed,
        "Diagnostic mode and raw screen persistence must be off by default.");

    Environment.SetEnvironmentVariable("HELPSYS_DIAGNOSTIC_MODE", "1");
    Environment.SetEnvironmentVariable("HELPSYS_DIAGNOSTIC_RAW_SCREEN", "I_UNDERSTAND_RAW_SCREEN_DATA");
    var diagnosticsRequested = new DiagnosticModePolicy();
    if (expectSafeProfile)
    {
        Assert(!diagnosticsRequested.Enabled && !diagnosticsRequested.RawScreenPersistenceAllowed,
            "Safe build must permanently disable raw diagnostic screen persistence.");
    }
    else
    {
        Assert(diagnosticsRequested.Enabled && diagnosticsRequested.RawScreenPersistenceAllowed,
            "Normal build requires both explicit diagnostic opt-ins before raw screen persistence may be considered.");
    }
}
finally
{
    Environment.SetEnvironmentVariable("HELPSYS_DIAGNOSTIC_MODE", oldDiagnostic);
    Environment.SetEnvironmentVariable("HELPSYS_DIAGNOSTIC_RAW_SCREEN", oldRawDiagnostic);
}

var telemetryProperties = typeof(PrivacySafeTelemetryEvent).GetProperties().Select(x => x.Name).OrderBy(x => x).ToArray();
var allowedTelemetryProperties = new[]
{
    "AppKind", "ErrorCode", "HelpSysVersion", "ResponseTimeMs", "SessionId", "Success", "SupportStage", "TaskKind"
}.OrderBy(x => x).ToArray();
Assert(telemetryProperties.SequenceEqual(allowedTelemetryProperties),
    "Long-term telemetry schema contains a field outside the privacy allowlist.");

var oldTelemetry = Environment.GetEnvironmentVariable("HELPSYS_TELEMETRY");
try
{
    Environment.SetEnvironmentVariable("HELPSYS_TELEMETRY", null);
    Assert(!new PrivacySafeTelemetry().Enabled, "Long-term telemetry must be disabled by default.");
}
finally
{
    Environment.SetEnvironmentVariable("HELPSYS_TELEMETRY", oldTelemetry);
}

if (expectSafeProfile)
    Assert(gate.Profile == PrivacyPolicyProfile.Safe, "Safe build did not compile with the Safe privacy profile.");
else
    Assert(gate.Profile == PrivacyPolicyProfile.Normal, "Normal build unexpectedly compiled with the Safe privacy profile.");

for (var i = 0; i < 1000; i++) gate.EvaluateState(safeContext, []);
var stopwatch = Stopwatch.StartNew();
for (var i = 0; i < 20_000; i++) gate.EvaluateState(safeContext, []);
stopwatch.Stop();

Assert(stopwatch.Elapsed < TimeSpan.FromSeconds(1),
    $"Privacy Gate hot path is too slow: {stopwatch.Elapsed.TotalMilliseconds:F0} ms / 20,000 checks.");

Console.WriteLine($"Privacy Gate runtime probe passed: {stopwatch.Elapsed.TotalMilliseconds:F0} ms / 20,000 checks; profile={gate.Profile}.");

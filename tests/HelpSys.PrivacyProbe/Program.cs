using System.Diagnostics;
using HelpSys.Models;
using HelpSys.Services;

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

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

var unknownContext = safeContext with { ForegroundProcess = "", ForegroundProcessId = 0 };
Assert(gate.EvaluateState(unknownContext, []).Classification == PrivacyClassification.Unknown,
    "UNKNOWN must not silently become SAFE.");

gate.SetManualPause(true);
Assert(gate.EvaluateState(safeContext, []).Classification == PrivacyClassification.ManualPause,
    "Manual screen-analysis pause must prevent egress.");
gate.SetManualPause(false);
Assert(gate.EvaluateState(safeContext, []).Classification == PrivacyClassification.Safe,
    "Manual pause must be resumable on a safe screen.");

var expectSafeProfile = args.Contains("--expect-safe-profile", StringComparer.OrdinalIgnoreCase);
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

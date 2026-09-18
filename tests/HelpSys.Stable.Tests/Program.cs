using HelpSys.Stable;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

static void Assert(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static void ExpectPlanner(Action action, string label)
{
    try { action(); }
    catch (PlannerException) { return; }
    throw new Exception("Expected PlannerException: " + label);
}

static void ExpectPrivacy(Action action, string label)
{
    try { action(); }
    catch (PrivacyBlockedException) { return; }
    throw new Exception("Expected PrivacyBlockedException: " + label);
}

UiControlSnapshot C(
    string id = "c1", string name = "Button", string type = "ControlType.Button",
    bool enabled = true, bool focused = false, bool focusable = true, bool password = false)
    => new(id, name, "", "", type, enabled, focused, focusable, password, 100, 100, 120, 60);

SafetyGate.EnsureSafeToCapture("notepad", "ordinary document", [C()]);
SafetyGate.EnsureSafeToCapture("msedge", "We use cookies", [C(name: "Accept cookies")]);
ExpectPrivacy(() => SafetyGate.EnsureSafeToCapture("notepad", "login", [C(password: true)]), "password");
ExpectPrivacy(() => SafetyGate.EnsureSafeToCapture("chrome", "Enter verification code", [C(type: "ControlType.Edit")]), "otp");
ExpectPrivacy(() => SafetyGate.EnsureSafeToCapture("chrome", "DevTools - Application - Cookies", [C()]), "cookies");
ExpectPrivacy(() => SafetyGate.EnsureSafeToCapture("chrome", "Your connection is not private", [C()]), "security warning");

var controls = new List<UiControlSnapshot> { C() };
GeminiPlannerClient.Validate(new("target","left_click","click",null,"c1",null,.99,0,0,0,0), controls);
ExpectPlanner(() => GeminiPlannerClient.Validate(new("target","left_click","パスワードを入力してください。",null,"c1",null,.99,0,0,0,0), controls), "secret guidance");
ExpectPlanner(() => GeminiPlannerClient.Validate(new("clarify","none","", "Enter your verification code.",null,null,.99,0,0,0,0), controls), "secret clarify");
ExpectPlanner(() => GeminiPlannerClient.Validate(new("target","left_click","Type your password.",null,"c1",null,.99,0,0,0,0), controls), "reversed secret guidance");
ExpectPlanner(() => GeminiPlannerClient.Validate(new("target","none","x",null,"c1",null,.99,0,0,0,0), controls), "target none");
ExpectPlanner(() => GeminiPlannerClient.Validate(new("target","launch_missiles","x",null,"c1",null,.99,0,0,0,0), controls), "unknown action");
ExpectPlanner(() => GeminiPlannerClient.Validate(new("target","type_text","type",null,"c1",null,.99,0,0,0,0), controls), "unfocused type");
var focused = new List<UiControlSnapshot> { C(type:"ControlType.Edit", focused:true) };
GeminiPlannerClient.Validate(new("target","type_text","type",null,"c1",null,.99,0,0,0,0), focused);
ExpectPlanner(() => GeminiPlannerClient.Validate(new("clarify","left_click","", "choose",null,null,.9,0,0,0,0), controls), "clarify action");
ExpectPlanner(() => GeminiPlannerClient.Validate(new("done","left_click","done",null,null,null,.9,0,0,0,0), controls), "done action");
ExpectPlanner(() => GeminiPlannerClient.Validate(new("done","none","done",null,null,null,.5,0,0,0,0), controls), "low done confidence");

Assert(UpdateService.IsTrustedReleaseUri(new Uri("https://github.com/syouziroupc/helpsys/releases/download/preview-latest/HelpSys-latest-win-x64.zip")), "canonical HelpSys release URI should be trusted");
Assert(!UpdateService.IsTrustedReleaseUri(new Uri("https://github.com/other/repo/releases/download/preview-latest/x.zip")), "foreign GitHub repo must not be trusted");
Assert(!UpdateService.IsTrustedReleaseUri(new Uri("https://github.com/syouziroupc/helpsys/releases/download/other-tag/x.zip")), "foreign tag must not be trusted");
Assert(!UpdateService.IsTrustedReleaseUri(new Uri("http://github.com/syouziroupc/helpsys/releases/download/preview-latest/x.zip")), "http must not be trusted");
Assert(!UpdateService.IsTrustedReleaseUri(new Uri("https://evil.example/a")), "foreign host must not be trusted");
Assert(VersionInfo.Version == "3.0.1", "test version drift");
Assert(!string.IsNullOrWhiteSpace(VersionInfo.BuildId), "build id missing");

var originalTarget = new UiControlSnapshot("u1","Go","GoButton","","ControlType.Button",true,false,true,false,100,100,120,60);
var sameTarget = originalTarget with { Id = "u99", X = 120, Y = 120 };
var movedTarget = originalTarget with { Id = "u99", X = 300, Y = 300 };
var disabledTarget = originalTarget with { Id = "u99", Enabled = false };
Assert(ObservationService.SameControlIdentity(originalTarget, sameTarget), "small layout drift should remain valid");
Assert(!ObservationService.SameControlIdentity(originalTarget, movedTarget), "moved target must invalidate stale overlay coordinates");
Assert(!ObservationService.SameControlIdentity(originalTarget, disabledTarget), "disabled target must invalidate guidance");

Assert(ObservationService.NormalizeBrowserDomain("https://Example.COM/path?q=secret") == "example.com", "full URL must reduce to host");
Assert(ObservationService.NormalizeBrowserDomain("example.com/private/path") == "example.com", "bare host must parse");
Assert(ObservationService.NormalizeBrowserDomain("127.0.0.1:8080/path") == "127.0.0.1", "IP host must parse");
Assert(ObservationService.NormalizeBrowserDomain("localhost:3000/test") == "localhost", "localhost must parse");
Assert(ObservationService.NormalizeBrowserDomain("printer setup") is null, "search text must not become a fake domain");
Assert(ObservationService.NormalizeBrowserDomain("settings") is null, "single search word must not become a fake domain");
Assert(ObservationService.LooksLikeAddressBar("Address and search bar", "ControlType.Edit"), "address bar must be recognized");
Assert(!ObservationService.LooksLikeAddressBar("Search", "ControlType.Edit"), "site search box must not be treated as address bar");
ExpectPlanner(() => GeminiPlannerClient.Validate(new("target","left_click","Proceed anyway to the unsafe site.",null,"c1",null,.99,0,0,0,0), controls), "warning bypass");


static string MakeTestImage(Rectangle patch)
{
    using var bitmap = new Bitmap(320, 200);
    using var graphics = Graphics.FromImage(bitmap);
    graphics.Clear(Color.FromArgb(25, 25, 25));
    using var brush = new SolidBrush(Color.FromArgb(235, 235, 235));
    graphics.FillRectangle(brush, patch);
    using var stream = new MemoryStream();
    bitmap.Save(stream, ImageFormat.Jpeg);
    return "data:image/jpeg;base64," + Convert.ToBase64String(stream.ToArray());
}

var visualPlan = new PlanResult("target","left_click","click",null,null,null,.95,100,200,220,260);
var visualBefore = MakeTestImage(new Rectangle(32,40,70,52));
var visualSame = MakeTestImage(new Rectangle(32,40,70,52));
var visualMoved = MakeTestImage(new Rectangle(210,40,70,52));
Assert(ObservationService.IsVisualTargetStillCurrent(visualBefore, visualSame, visualPlan), "unchanged visual target must stay valid");
Assert(!ObservationService.IsVisualTargetStillCurrent(visualBefore, visualMoved, visualPlan), "moved visual target must invalidate stale guidance");

Console.WriteLine("HelpSys Stable C# safety/validation/visual-state tests passed.");

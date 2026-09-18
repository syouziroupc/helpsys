using HelpSys.Stable;

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
ExpectPlanner(() => GeminiPlannerClient.Validate(new("target","none","x",null,"c1",null,.99,0,0,0,0), controls), "target none");
ExpectPlanner(() => GeminiPlannerClient.Validate(new("target","launch_missiles","x",null,"c1",null,.99,0,0,0,0), controls), "unknown action");
ExpectPlanner(() => GeminiPlannerClient.Validate(new("target","type_text","type",null,"c1",null,.99,0,0,0,0), controls), "unfocused type");
var focused = new List<UiControlSnapshot> { C(type:"ControlType.Edit", focused:true) };
GeminiPlannerClient.Validate(new("target","type_text","type",null,"c1",null,.99,0,0,0,0), focused);
ExpectPlanner(() => GeminiPlannerClient.Validate(new("clarify","left_click","", "choose",null,null,.9,0,0,0,0), controls), "clarify action");
ExpectPlanner(() => GeminiPlannerClient.Validate(new("done","left_click","done",null,null,null,.9,0,0,0,0), controls), "done action");
ExpectPlanner(() => GeminiPlannerClient.Validate(new("done","none","done",null,null,null,.5,0,0,0,0), controls), "low done confidence");

Assert(UpdateService.IsTrustedReleaseUri(new Uri("https://github.com/a/b")), "github release URI should be trusted");
Assert(!UpdateService.IsTrustedReleaseUri(new Uri("http://github.com/a/b")), "http must not be trusted");
Assert(!UpdateService.IsTrustedReleaseUri(new Uri("https://evil.example/a")), "foreign host must not be trusted");
Assert(VersionInfo.Version == "3.0.1", "test version drift");
Assert(!string.IsNullOrWhiteSpace(VersionInfo.BuildId), "build id missing");

Console.WriteLine("HelpSys Stable C# safety/validation tests passed.");


var originalTarget = new UiControlSnapshot("u1","Go","GoButton","","ControlType.Button",true,false,true,false,100,100,120,60);
var sameTarget = originalTarget with { Id = "u99", X = 120, Y = 120 };
var movedTarget = originalTarget with { Id = "u99", X = 300, Y = 300 };
var disabledTarget = originalTarget with { Id = "u99", Enabled = false };
Assert(ObservationService.SameControlIdentity(originalTarget, sameTarget), "small layout drift should remain valid");
Assert(!ObservationService.SameControlIdentity(originalTarget, movedTarget), "moved target must invalidate stale overlay coordinates");
Assert(!ObservationService.SameControlIdentity(originalTarget, disabledTarget), "disabled target must invalidate guidance");

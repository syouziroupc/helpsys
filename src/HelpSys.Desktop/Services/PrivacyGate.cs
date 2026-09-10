using System.Text.RegularExpressions;
using HelpSys.Models;

namespace HelpSys.Services;

public enum PrivacyClassification
{
    Safe,
    Blocked,
    Unknown,
    ManualPause
}

public enum PrivacyPolicyProfile
{
    Normal,
    Safe
}

public sealed record PrivacyAssessment(
    PrivacyClassification Classification,
    string ReasonCode,
    string UserMessage)
{
    public bool CanSend => Classification == PrivacyClassification.Safe;
}

public sealed record PrivacyApproval(PrivacyAssessment Assessment, object? Body)
{
    public bool CanSend => Assessment.CanSend && Body is not null;
}

/// <summary>
/// Lightweight, local-only egress policy. It never calls a model, network API, file store,
/// clipboard, browser profile, credential store, cookie store, localStorage or sessionStorage.
/// </summary>
public sealed class PrivacyGate
{
    private static readonly string[] PasswordManagerTerms =
    [
        "1password", "bitwarden", "keepass", "lastpass", "dashlane", "keeper password", "nordpass", "proton pass",
        "パスワードマネージャー", "パスワード管理"
    ];

    private static readonly string[] SecretTerms =
    [
        "password", "passwd", "passcode", "パスワード", "暗証番号", "pin code",
        "api key", "apikey", "apiキー", "client secret", "secret key", "access token", "session token",
        "refresh token", "bearer token", "秘密鍵", "private key", "begin private key", "backup code", "recovery code"
    ];

    private static readonly string[] OtpTerms =
    [
        "one-time password", "one time password", "one-time code", "verification code", "security code",
        "otp", "totp", "2fa", "mfa", "ワンタイム", "認証コード", "確認コード", "二段階認証", "多要素認証"
    ];

    private static readonly string[] StorageTerms =
    [
        "cookies", "cookie", "local storage", "localstorage", "session storage", "sessionstorage",
        "application storage", "storage inspector", "クッキー", "ローカルストレージ", "セッションストレージ"
    ];

    private static readonly string[] DevToolsTerms =
    [
        "devtools", "developer tools", "開発者ツール"
    ];

    private static readonly string[] FinanceTerms =
    [
        "internet banking", "online banking", "banking", "ネットバンク", "インターネットバンキング", "銀行",
        "証券", "brokerage", "securities", "trading account", "取引口座"
    ];

    private static readonly string[] FinanceActionTerms =
    [
        "login", "log in", "sign in", "signin", "ログイン", "認証", "本人確認", "取引", "注文", "売買", "振込", "送金"
    ];

    private static readonly string[] CardAuthTerms =
    [
        "3d secure", "3-d secure", "本人認証サービス", "カード認証", "card verification", "cvv", "cvc", "カード番号"
    ];

    private static readonly HashSet<string> OsAuthProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "CredentialUIBroker", "LogonUI", "consent", "credui"
    };

    private static readonly Regex EmailRegex = new(
        @"(?<![\w.+-])[\w.+-]+@[\w.-]+\.[A-Za-z]{2,}(?![\w.-])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private bool _manualPause;
    private PrivacyClassification _lastClassification = PrivacyClassification.Unknown;

    public PrivacyPolicyProfile Profile { get; } = ResolveProfile();
    public bool ManualPause => _manualPause;
    public bool PrivacyMode => _manualPause || _lastClassification is PrivacyClassification.Blocked or PrivacyClassification.Unknown;

    public void SetManualPause(bool paused)
    {
        _manualPause = paused;
        if (paused) _lastClassification = PrivacyClassification.ManualPause;
    }

    public PrivacyAssessment EvaluateState(SystemContextSnapshot systemContext, IReadOnlyList<UiElementCandidate> elements)
    {
        if (_manualPause)
            return Remember(new PrivacyAssessment(
                PrivacyClassification.ManualPause,
                "manual_pause",
                "画面解析は利用者によって停止されています。"));

        try
        {
            if (systemContext.ForegroundProcessId <= 0 || string.IsNullOrWhiteSpace(systemContext.ForegroundProcess))
                return Remember(Unknown("foreground_unknown", "操作中の画面を安全に特定できないため、クラウド画面解析を停止しています。"));

            if (elements.Any(x => x.Password))
                return Remember(Block("password_control", "パスワード入力中は画面情報をクラウドへ送りません。入力内容はHelpSysでは確認しません。"));

            if (OsAuthProcesses.Contains(systemContext.ForegroundProcess))
                return Remember(Block("os_authentication", "Windowsの認証画面ではクラウド画面解析を停止します。認証内容はHelpSysでは確認しません。"));

            var browserUrl = systemContext.Browser?.Url;
            if (!string.IsNullOrWhiteSpace(browserUrl) && !Uri.TryCreate(browserUrl, UriKind.Absolute, out _))
                return Remember(Unknown("browser_url_unparseable", "ブラウザ画面の状態を安全に判定できないため、クラウド画面解析を停止しています。"));

            var texts = EnumerateLocalSignals(systemContext, elements).ToArray();

            if (ContainsAny(texts, PasswordManagerTerms))
                return Remember(Block("password_manager", "パスワード管理画面ではクラウド画面解析を停止します。"));

            if (ContainsAny(texts, OtpTerms))
                return Remember(Block("otp_or_mfa", "認証コード・二段階認証の画面ではクラウド画面解析を停止します。"));

            if (ContainsAny(texts, SecretTerms))
                return Remember(Block("secret_material", "パスワード・トークン・APIキー等の秘密情報が表示される可能性があるため、クラウド画面解析を停止します。"));

            var devTools = ContainsAny(texts, DevToolsTerms);
            var storage = ContainsAny(texts, StorageTerms);
            if (storage || (devTools && Profile == PrivacyPolicyProfile.Safe))
                return Remember(Block("browser_secret_storage", "Cookie・Storage等を扱う画面ではクラウド画面解析を停止します。"));

            var finance = ContainsAny(texts, FinanceTerms);
            var financeAction = ContainsAny(texts, FinanceActionTerms);
            if ((finance && financeAction) || (finance && Profile == PrivacyPolicyProfile.Safe))
                return Remember(Block("financial_service", "金融・証券の認証または取引画面ではクラウド画面解析を停止します。"));

            if (ContainsAny(texts, CardAuthTerms))
                return Remember(Block("card_authentication", "カード認証情報を扱う画面ではクラウド画面解析を停止します。"));

            return Remember(new PrivacyAssessment(
                PrivacyClassification.Safe,
                "safe",
                "画面解析中"));
        }
        catch
        {
            return Remember(Unknown("privacy_gate_failure", "安全判定を完了できないため、画面情報をクラウドへ送りません。"));
        }
    }

    public PrivacyApproval ApproveQuality(
        string request,
        ScreenCaptureFrame frame,
        IReadOnlyList<UiElementCandidate> elements,
        IReadOnlyList<GuideHistoryItem> history,
        SystemContextSnapshot systemContext,
        bool recoveryMode,
        string? routeIssue)
    {
        var assessment = EvaluateState(systemContext, elements);
        if (!assessment.CanSend) return new PrivacyApproval(assessment, null);

        var body = new
        {
            request = SanitizeGeneralText(request, 900),
            history = CompactHistory(history),
            systemContext = CompactSystemContext(systemContext),
            evidence = GuidanceEvidenceService.Build(true, elements, history, systemContext),
            recoveryMode,
            routeIssue = SanitizeGeneralText(routeIssue, 180),
            elements = elements.Take(280).Select(CompactElement),
            image = frame.ImageDataUri,
            imageWidth = frame.ImageWidth,
            imageHeight = frame.ImageHeight
        };
        return new PrivacyApproval(assessment, body);
    }

    public PrivacyApproval ApproveStructured(
        string request,
        IReadOnlyList<UiElementCandidate> elements,
        IReadOnlyList<GuideHistoryItem> history,
        SystemContextSnapshot systemContext)
    {
        var assessment = EvaluateState(systemContext, elements);
        if (!assessment.CanSend) return new PrivacyApproval(assessment, null);

        var body = new
        {
            request = SanitizeGeneralText(request, 900),
            history = CompactHistory(history),
            systemContext = CompactSystemContext(systemContext),
            evidence = GuidanceEvidenceService.Build(false, elements, history, systemContext),
            elements = elements.Take(280).Select(CompactElement)
        };
        return new PrivacyApproval(assessment, body);
    }

    public PrivacyApproval ApproveVision(
        string request,
        ScreenCaptureFrame frame,
        IReadOnlyList<UiElementCandidate> elements,
        IReadOnlyList<GuideHistoryItem> history,
        SystemContextSnapshot systemContext)
    {
        var assessment = EvaluateState(systemContext, elements);
        if (!assessment.CanSend) return new PrivacyApproval(assessment, null);

        var body = new
        {
            request = SanitizeGeneralText(request, 900),
            history = CompactHistory(history),
            systemContext = CompactSystemContext(systemContext),
            evidence = GuidanceEvidenceService.Build(true, elements, history, systemContext),
            image = frame.ImageDataUri,
            imageWidth = frame.ImageWidth,
            imageHeight = frame.ImageHeight
        };
        return new PrivacyApproval(assessment, body);
    }

    private static object CompactElement(UiElementCandidate x) => new
    {
        id = x.Id,
        name = SanitizeGeneralText(x.Name, 140),
        automationId = SanitizeGeneralText(x.AutomationId, 120),
        className = SanitizeGeneralText(x.ClassName, 120),
        controlType = x.ControlType,
        processName = x.ProcessName,
        interactable = x.Interactable,
        enabled = x.Enabled,
        keyboardFocusable = x.KeyboardFocusable,
        focused = x.Focused,
        password = false,
        inputPresent = !x.Password && !string.IsNullOrEmpty(x.Value),
        toggleState = x.ToggleState,
        selected = x.Selected,
        expandCollapseState = x.ExpandCollapseState,
        x = x.X,
        y = x.Y,
        width = x.Width,
        height = x.Height
    };

    private static object[] CompactHistory(IReadOnlyList<GuideHistoryItem> history) => history
        .TakeLast(12)
        .Select(x => (object)new
        {
            step = x.Step,
            action = SanitizeGeneralText(x.Action, 80),
            targetName = SanitizeGeneralText(x.TargetName, 120),
            instruction = SanitizeGeneralText(x.Instruction, 220)
        })
        .ToArray();

    private static object CompactSystemContext(SystemContextSnapshot context)
    {
        var browserDomain = context.Browser?.Domain;
        if (string.IsNullOrWhiteSpace(browserDomain) && Uri.TryCreate(context.Browser?.Url, UriKind.Absolute, out var uri))
            browserDomain = uri.Host;

        return new
        {
            foregroundProcess = context.ForegroundProcess,
            foregroundTitle = SanitizeGeneralText(context.ForegroundTitle, 140),
            foregroundProcessId = context.ForegroundProcessId,
            taskbarVisible = context.TaskbarVisible,
            runningApps = context.RunningApps.Take(20).Select(x => SanitizeGeneralText(x, 80)).ToArray(),
            browser = context.Browser is null ? null : new
            {
                processName = context.Browser.ProcessName,
                domain = SanitizeGeneralText(browserDomain, 160),
                https = context.Browser.Https,
                addressFieldFocused = context.Browser.AddressFieldFocused
            }
        };
    }

    private static IEnumerable<string> EnumerateLocalSignals(SystemContextSnapshot context, IReadOnlyList<UiElementCandidate> elements)
    {
        yield return context.ForegroundProcess;
        yield return context.ForegroundTitle;
        if (context.Browser is not null)
        {
            yield return context.Browser.ProcessName;
            yield return context.Browser.WindowTitle;
            yield return context.Browser.Domain ?? string.Empty;
            yield return context.Browser.Url ?? string.Empty;
        }

        foreach (var element in elements.Take(420))
        {
            yield return element.Name;
            yield return element.AutomationId;
            yield return element.ClassName;
            yield return element.ControlType;
            yield return element.ProcessName;
        }
    }

    private static bool ContainsAny(IEnumerable<string> texts, IReadOnlyList<string> terms)
    {
        foreach (var text in texts)
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            foreach (var term in terms)
            {
                if (text.Contains(term, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        return false;
    }

    private PrivacyAssessment Remember(PrivacyAssessment assessment)
    {
        _lastClassification = assessment.Classification;
        return assessment;
    }

    private static PrivacyAssessment Block(string code, string message) =>
        new(PrivacyClassification.Blocked, code, message);

    private static PrivacyAssessment Unknown(string code, string message) =>
        new(PrivacyClassification.Unknown, code, message);

    private static string? SanitizeGeneralText(string? text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var value = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        value = EmailRegex.Replace(value, "<email>");
        return value.Length <= maxLength ? value : value[..maxLength];
    }

    private static PrivacyPolicyProfile ResolveProfile()
    {
#if HELPSYS_SAFE_BUILD
        return PrivacyPolicyProfile.Safe;
#else
        return PrivacyPolicyProfile.Normal;
#endif
    }
}

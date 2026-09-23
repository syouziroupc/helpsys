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
    Safe,
    Outlaw
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
/// Lightweight local-only egress policy. Secrets, authentication, browser secret stores and
/// transactional finance remain fail-closed. Ordinary search/form context is not discarded merely
/// because a page mentions a security-related word; non-secret evidence is sanitized at egress.
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

    private static readonly Regex JapanesePhoneRegex = new(
        @"(?<!\d)(?:(?:0[5789]0[- ]?\d{4}[- ]?\d{4})|(?:0\d{1,4}[- ]\d{1,4}[- ]\d{3,4})|(?:\+81[- ]?[1-9]\d{0,4}[- ]?\d{1,4}[- ]?\d{3,4}))(?!\d)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex PostalCodeRegex = new(
        @"(?<!\d)〒?\s*\d{3}-\d{4}(?!\d)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex LabeledSecretRegex = new(
        @"(?i)\b(password|passwd|passcode|otp|totp|2fa|mfa|api[ _-]?key|client[ _-]?secret|access[ _-]?token|refresh[ _-]?token|session[ _-]?token|backup[ _-]?code|recovery[ _-]?code)\b\s*[:=]\s*([^\s,;]{3,})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex BearerRegex = new(
        @"(?i)\bbearer\s+[A-Za-z0-9._~+/=-]{8,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex JwtRegex = new(
        @"\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex KnownApiKeyRegex = new(
        @"(?i)\b(?:sk-[A-Za-z0-9_-]{16,}|gh[pousr]_[A-Za-z0-9]{20,}|AIza[A-Za-z0-9_-]{20,}|AKIA[0-9A-Z]{16})\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex PrivateKeyRegex = new(
        @"-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex CardNumberRegex = new(
        @"(?<!\d)(?:\d[ -]?){13,19}(?!\d)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex UrlRegex = new(
        @"https?://[^\s<>""']+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

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
        if (OutlawModePolicy.Enabled)
            return Remember(new PrivacyAssessment(
                PrivacyClassification.Safe,
                "outlaw_bypass",
                "無法者版ではセキュリティ判定による停止を行いません。"));

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

            var texts = EnumerateLocalSignals(systemContext, elements).ToArray();
            var browserUrl = systemContext.Browser?.Url ?? string.Empty;
            var hasOrdinaryInput = elements.Any(IsNonSearchInput);

            if (browserUrl.Contains("://password", StringComparison.OrdinalIgnoreCase) || ContainsAny(texts, PasswordManagerTerms))
                return Remember(Block("password_manager", "パスワード管理画面ではクラウド画面解析を停止します。"));

            // A security word in documentation/help text is not itself secret material. Stop when
            // an actual input surface is present or a high-confidence secret value is visible.
            if (ContainsHighConfidenceSecretValue(texts))
                return Remember(Block("secret_material", "トークン・APIキー・秘密鍵等の秘密情報を検出したため、クラウド画面解析を停止します。"));

            if (ContainsAny(texts, OtpTerms) && (hasOrdinaryInput || HasSensitiveInputSemantic(elements, OtpTerms) || (ContainsAny(new[] { systemContext.ForegroundTitle ?? string.Empty }, OtpTerms) && ContainsAny(new[] { systemContext.ForegroundTitle ?? string.Empty }, new[] { "enter", "type", "verify", "入力", "認証", "確認" }))))
                return Remember(Block("otp_or_mfa", "認証コード・二段階認証の入力画面ではクラウド画面解析を停止します。"));

            if (ContainsAny(texts, SecretTerms) && (hasOrdinaryInput || HasSensitiveInputSemantic(elements, SecretTerms)))
                return Remember(Block("secret_material", "パスワード・トークン・APIキー等の秘密情報を入力する画面ではクラウド画面解析を停止します。"));

            var devTools = ContainsAny(texts, DevToolsTerms);
            var storage = ContainsAny(texts, StorageTerms);
            var internalStoragePage = browserUrl.Contains("localstorage", StringComparison.OrdinalIgnoreCase) ||
                                      browserUrl.Contains("cookie", StringComparison.OrdinalIgnoreCase) ||
                                      browserUrl.Contains("storage", StringComparison.OrdinalIgnoreCase);
            if (internalStoragePage || (devTools && storage))
                return Remember(Block("browser_secret_storage", "Cookie・Storage等の秘密ストアを扱う画面ではクラウド画面解析を停止します。"));

            var finance = ContainsAny(texts, FinanceTerms);
            var financeAction = ContainsAny(texts, FinanceActionTerms);
            if (finance && financeAction)
                return Remember(Block("financial_service", "金融・証券の認証または取引画面ではクラウド画面解析を停止します。"));

            if ((ContainsAny(texts, CardAuthTerms) && hasOrdinaryInput) || ContainsCardNumber(texts))
                return Remember(Block("card_authentication", "カード認証情報を扱う画面ではクラウド画面解析を停止します。"));

            // Browser address/search text that cannot be parsed as a URL is commonly just a search
            // query. It is not an UNKNOWN state by itself. Egress sanitization below still removes
            // contact PII, URL secrets, tokens and card data before structured evidence is sent.
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
            request = SanitizeOutboundText(request, 900),
            history = CompactHistory(history),
            systemContext = CompactSystemContext(systemContext),
            evidence = CompactEvidence(true, elements, history, systemContext),
            recoveryMode,
            routeIssue = SanitizeOutboundText(routeIssue, 180),
            elements = elements.Take(280).Select(CompactElement),
            image = frame.ImageDataUri,
            imageWidth = frame.ImageWidth,
            imageHeight = frame.ImageHeight
        };
        return new PrivacyApproval(assessment, body);
    }

    public PrivacyApproval ApproveStablePlanCompatibility(
        string request,
        ScreenCaptureFrame frame,
        IReadOnlyList<UiElementCandidate> elements,
        SystemContextSnapshot systemContext)
    {
        var assessment = EvaluateState(systemContext, elements);
        if (!assessment.CanSend) return new PrivacyApproval(assessment, null);

        var body = new
        {
            goal = SanitizeOutboundText(request, 900),
            processName = SanitizeOutboundText(systemContext.ForegroundProcess, 80),
            windowTitle = SanitizeOutboundText(systemContext.ForegroundTitle, 260),
            browserDomain = SanitizeOutboundText(systemContext.Browser?.Domain, 220),
            controls = elements.Take(96).Select(x => new
            {
                id = x.Id,
                name = SanitizeOutboundText(x.Name, 180),
                automationId = SanitizeOutboundText(x.AutomationId, 120),
                className = SanitizeOutboundText(x.ClassName, 120),
                controlType = x.ControlType,
                enabled = x.Enabled,
                focused = x.Focused,
                keyboardFocusable = x.KeyboardFocusable,
                x = NormalizeStableCoordinate(x.X - frame.ScreenX, frame.ScreenWidth),
                y = NormalizeStableCoordinate(x.Y - frame.ScreenY, frame.ScreenHeight),
                width = NormalizeStableCoordinate(x.Width, frame.ScreenWidth),
                height = NormalizeStableCoordinate(x.Height, frame.ScreenHeight)
            }).ToArray(),
            image = frame.ImageDataUri
        };

        return new PrivacyApproval(assessment, body);
    }

    private static double NormalizeStableCoordinate(double value, double total)
        => total <= 0 ? 0 : Math.Clamp(value / total * 1000d, 0d, 1000d);

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
            request = SanitizeOutboundText(request, 900),
            history = CompactHistory(history),
            systemContext = CompactSystemContext(systemContext),
            evidence = CompactEvidence(false, elements, history, systemContext),
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
            request = SanitizeOutboundText(request, 900),
            history = CompactHistory(history),
            systemContext = CompactSystemContext(systemContext),
            evidence = CompactEvidence(true, elements, history, systemContext),
            image = frame.ImageDataUri,
            imageWidth = frame.ImageWidth,
            imageHeight = frame.ImageHeight
        };
        return new PrivacyApproval(assessment, body);
    }

    private static object CompactElement(UiElementCandidate x) => new
    {
        id = x.Id,
        name = SanitizeOutboundText(x.Name, x.Interactable ? 180 : 360),
        automationId = SanitizeOutboundText(x.AutomationId, 120),
        className = SanitizeOutboundText(x.ClassName, 120),
        controlType = x.ControlType,
        processName = x.ProcessName,
        interactable = x.Interactable,
        enabled = x.Enabled,
        keyboardFocusable = x.KeyboardFocusable,
        focused = x.Focused,
        password = false,
        inputPresent = !x.Password && !string.IsNullOrEmpty(x.Value),
        inputValue = SafeOutboundInputValue(x),
        toggleState = SanitizeOutboundText(x.ToggleState, 40),
        selected = x.Selected,
        expandCollapseState = SanitizeOutboundText(x.ExpandCollapseState, 40),
        x = x.X,
        y = x.Y,
        width = x.Width,
        height = x.Height
    };

    private static string? SafeOutboundInputValue(UiElementCandidate element)
    {
        if (element.Password || string.IsNullOrWhiteSpace(element.Value)) return null;
        var role = element.AutomationId ?? string.Empty;
        var usefulSearchContext = role.Contains("role:browser_address", StringComparison.OrdinalIgnoreCase) ||
                                  role.Contains("role:web_search", StringComparison.OrdinalIgnoreCase) ||
                                  role.Contains("role:windows_search", StringComparison.OrdinalIgnoreCase);
        return usefulSearchContext ? SanitizeOutboundText(element.Value, 240) : null;
    }

    private static object[] CompactHistory(IReadOnlyList<GuideHistoryItem> history) => history
        .TakeLast(12)
        .Select(x => (object)new
        {
            step = x.Step,
            action = SanitizeOutboundText(x.Action, 80),
            targetName = SanitizeOutboundText(x.TargetName, 120),
            instruction = SanitizeOutboundText(x.Instruction, 220)
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
            foregroundTitle = SanitizeOutboundText(context.ForegroundTitle, 140),
            foregroundProcessId = context.ForegroundProcessId,
            taskbarVisible = context.TaskbarVisible,
            runningApps = context.RunningApps.Take(20).Select(x => SanitizeOutboundText(x, 80)).ToArray(),
            browser = context.Browser is null ? null : new
            {
                processName = context.Browser.ProcessName,
                domain = SanitizeOutboundText(browserDomain, 160),
                https = context.Browser.Https,
                addressFieldFocused = context.Browser.AddressFieldFocused
            }
        };
    }

    private static object CompactEvidence(
        bool screenshotAvailable,
        IReadOnlyList<UiElementCandidate> elements,
        IReadOnlyList<GuideHistoryItem> history,
        SystemContextSnapshot context)
    {
        var evidence = GuidanceEvidenceService.Build(screenshotAvailable, elements, history, context);
        return new
        {
            screenshotAvailable = evidence.ScreenshotAvailable,
            uiElementCount = evidence.UiElementCount,
            interactableCount = evidence.InteractableCount,
            focusedCount = evidence.FocusedCount,
            focusedElements = evidence.FocusedElements.Select(x => SanitizeOutboundText(x, 160)).ToArray(),
            foregroundProcess = evidence.ForegroundProcess,
            foregroundTitle = SanitizeOutboundText(evidence.ForegroundTitle, 140),
            taskbarVisible = evidence.TaskbarVisible,
            runningAppCount = evidence.RunningAppCount,
            browserDomain = SanitizeOutboundText(evidence.BrowserDomain, 160),
            browserAddressFocused = evidence.BrowserAddressFocused,
            historyCount = evidence.HistoryCount,
            recentTargets = evidence.RecentTargets.Select(x => SanitizeOutboundText(x, 140)).ToArray(),
            evidenceSources = evidence.EvidenceSources
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
            yield return element.Value ?? string.Empty;
        }
    }

    private static bool IsNonSearchInput(UiElementCandidate element)
    {
        if (!element.Interactable || element.Password || element.ControlType is not ("Edit" or "ComboBox")) return false;
        var role = element.AutomationId ?? string.Empty;
        return !role.Contains("role:browser_address", StringComparison.OrdinalIgnoreCase) &&
               !role.Contains("role:web_search", StringComparison.OrdinalIgnoreCase) &&
               !role.Contains("role:windows_search", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasSensitiveInputSemantic(
        IReadOnlyList<UiElementCandidate> elements,
        IReadOnlyList<string> terms)
    {
        foreach (var element in elements)
        {
            if (!IsNonSearchInput(element)) continue;
            var signals = new[] { element.Name, element.AutomationId, element.ClassName, element.Value ?? string.Empty };
            if (ContainsAny(signals, terms)) return true;
        }
        return false;
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

    private static bool ContainsHighConfidenceSecretValue(IEnumerable<string> texts)
    {
        foreach (var text in texts)
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            if (BearerRegex.IsMatch(text) || JwtRegex.IsMatch(text) || KnownApiKeyRegex.IsMatch(text) || PrivateKeyRegex.IsMatch(text))
                return true;
        }
        return false;
    }

    private static bool ContainsCardNumber(IEnumerable<string> texts)
    {
        foreach (var text in texts)
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            foreach (Match match in CardNumberRegex.Matches(text))
            {
                var digits = new string(match.Value.Where(char.IsDigit).ToArray());
                if (digits.Length is >= 13 and <= 19 && PassesLuhn(digits)) return true;
            }
        }
        return false;
    }

    private static bool PassesLuhn(string digits)
    {
        var sum = 0;
        var alternate = false;
        for (var i = digits.Length - 1; i >= 0; i--)
        {
            var n = digits[i] - '0';
            if (alternate)
            {
                n *= 2;
                if (n > 9) n -= 9;
            }
            sum += n;
            alternate = !alternate;
        }
        return sum % 10 == 0;
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

    private static string? SanitizeOutboundText(string? text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var value = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        value = UrlRegex.Replace(value, StripUrlSecrets);
        value = EmailRegex.Replace(value, "<email>");
        value = JapanesePhoneRegex.Replace(value, "<phone>");
        value = PostalCodeRegex.Replace(value, "<postal-code>");
        value = LabeledSecretRegex.Replace(value, "$1=<redacted-secret>");
        value = BearerRegex.Replace(value, "Bearer <redacted-secret>");
        value = JwtRegex.Replace(value, "<redacted-jwt>");
        value = KnownApiKeyRegex.Replace(value, "<redacted-api-key>");
        value = PrivateKeyRegex.Replace(value, "<redacted-private-key>");
        value = CardNumberRegex.Replace(value, match =>
        {
            var digits = new string(match.Value.Where(char.IsDigit).ToArray());
            return digits.Length is >= 13 and <= 19 && PassesLuhn(digits) ? "<redacted-card>" : match.Value;
        });
        return value.Length <= maxLength ? value : value[..maxLength];
    }

    private static string StripUrlSecrets(Match match)
    {
        if (!Uri.TryCreate(match.Value, UriKind.Absolute, out var uri)) return "<url>";
        return uri.GetLeftPart(UriPartial.Authority);
    }

    private static PrivacyPolicyProfile ResolveProfile()
    {
#if HELPSYS_OUTLAW_BUILD
        return PrivacyPolicyProfile.Outlaw;
#elif HELPSYS_SAFE_BUILD
        return PrivacyPolicyProfile.Safe;
#else
        return PrivacyPolicyProfile.Normal;
#endif
    }
}

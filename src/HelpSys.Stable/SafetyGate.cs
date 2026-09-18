using System.Text.RegularExpressions;

namespace HelpSys.Stable;

internal static class SafetyGate
{
    private static readonly HashSet<string> BlockedProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "CredentialUIBroker", "LogonUI", "consent", "credui"
    };

    private static readonly string[] SecurityWarningTerms =
    [
        "your connection is not private", "deceptive site", "dangerous site", "certificate error",
        "接続はプライベートではありません", "偽のサイト", "危険なサイト", "証明書エラー"
    ];

    private static readonly string[] SecretContextTerms =
    [
        "one-time password", "verification code", "security code", "recovery key", "private key",
        "api key", "client secret", "access token", "refresh token", "cvv", "cvc",
        "ワンタイム", "認証コード", "確認コード", "リカバリキー", "秘密鍵", "apiキー", "カード番号", "セキュリティコード"
    ];

    private static readonly string[] SensitiveStorageTerms =
    [
        "cookie storage", "session storage", "local storage",
        "application - cookies", "application > cookies", "devtools - application - cookies",
        "cookie・storage", "cookie / storage", "セッションストレージ", "ローカルストレージ"
    ];

    public static void EnsureSafeToCapture(
        string processName,
        string windowTitle,
        IReadOnlyList<UiControlSnapshot> controls)
    {
        if (BlockedProcesses.Contains(processName))
            throw new PrivacyBlockedException("Windowsの認証画面では画面解析を停止します。認証内容はHelpSysへ送信しません。");

        if (controls.Any(x => x.Password))
            throw new PrivacyBlockedException("パスワード入力欄を検出したため、スクリーンショットを撮影せず画面解析を停止しました。");

        var signals = string.Join(' ', new[] { windowTitle }.Concat(controls.Select(x => x.Name)).Take(220));
        if (ContainsAny(signals, SecurityWarningTerms))
            throw new PrivacyBlockedException("ブラウザまたはOSのセキュリティ警告画面では自動案内を停止します。警告を迂回する操作は案内しません。");

        if (ContainsAny(signals, SensitiveStorageTerms))
            throw new PrivacyBlockedException("Cookie・ブラウザストレージなどの機密情報画面を検出したため、スクリーンショットを撮影せず解析を停止しました。");

        var hasEditableControl = controls.Any(x =>
            x.Enabled && x.KeyboardFocusable &&
            (x.ControlType.Contains("Edit", StringComparison.OrdinalIgnoreCase) ||
             x.ControlType.Contains("Document", StringComparison.OrdinalIgnoreCase)));

        if (hasEditableControl && ContainsAny(signals, SecretContextTerms))
            throw new PrivacyBlockedException("認証コード・秘密鍵・カード認証などの機密入力画面を検出したため、スクリーンショットを撮影せず解析を停止しました。");
    }

    internal static bool ContainsWarningBypass(string? value)
        => Regex.IsMatch(
            value ?? "",
            @"(proceed\s+anyway|continue\s+to\s+(?:the\s+)?site|ignore.{0,24}warning|bypass.{0,24}(warning|certificate|smartscreen)|advanced.{0,24}proceed|警告.{0,24}無視|無視して.{0,24}(続|進)|詳細設定.{0,24}(続行|アクセス|進)|安全ではありません.{0,24}(続|進)|危険.{0,24}続行|このサイトに進む)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static bool ContainsSecretRequest(string? value)
        => Regex.IsMatch(
            value ?? "",
            @"(?:(password|passcode|パスワード|暗証|\bpin\b|otp|ワンタイム|認証コード|verification\s*code|recovery\s*key|リカバリ(?:ー)?キー|秘密鍵|private\s*key|api\s*key|apiキー|cvv|cvc|セキュリティコード).{0,36}(教え|送|貼|入力|記入|tell|send|paste|enter|type|provide)|(教え|送|貼|入力|記入|tell|send|paste|enter|type|provide).{0,36}(password|passcode|パスワード|暗証|\bpin\b|otp|ワンタイム|認証コード|verification\s*code|recovery\s*key|リカバリ(?:ー)?キー|秘密鍵|private\s*key|api\s*key|apiキー|cvv|cvc|セキュリティコード))",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool ContainsAny(string text, IEnumerable<string> terms)
        => terms.Any(term => text.Contains(term, StringComparison.OrdinalIgnoreCase));
}

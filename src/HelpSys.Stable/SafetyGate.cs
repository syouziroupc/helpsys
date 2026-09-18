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
        "cookies", "cookie storage", "session storage", "local storage",
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

    private static bool ContainsAny(string text, IEnumerable<string> terms)
        => terms.Any(term => text.Contains(term, StringComparison.OrdinalIgnoreCase));
}

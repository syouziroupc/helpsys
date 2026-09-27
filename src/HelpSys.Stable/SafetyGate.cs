using System.Diagnostics;

namespace HelpSys.Stable;

internal static class SafetyGate
{
    private static readonly HashSet<string> BlockedProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "CredentialUIBroker", "LogonUI", "consent", "credui"
    };

    private static readonly HashSet<string> LowRiskShellProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost", "TextInputHost", "ctfmon"
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

    public static void EnsureNoForeignOverlay(
        nint targetWindow,
        int targetProcessId,
        NativeMethods.Rect targetRect)
    {
        var current = NativeMethods.GetWindow(targetWindow, NativeMethods.GwHwndPrev);
        var visited = 0;
        while (current != nint.Zero && visited++ < 64)
        {
            if (NativeMethods.IsWindow(current) && NativeMethods.IsWindowVisible(current))
            {
                NativeMethods.GetWindowThreadProcessId(current, out var rawPid);
                var pid = checked((int)rawPid);
                if (pid > 0 && pid != targetProcessId && pid != Environment.ProcessId &&
                    NativeMethods.GetWindowRect(current, out var rect))
                {
                    var overlapWidth = Math.Max(0, Math.Min(targetRect.Right, rect.Right) - Math.Max(targetRect.Left, rect.Left));
                    var overlapHeight = Math.Max(0, Math.Min(targetRect.Bottom, rect.Bottom) - Math.Max(targetRect.Top, rect.Top));
                    var overlapArea = (long)overlapWidth * overlapHeight;
                    if (overlapArea >= 10_000)
                    {
                        var processName = GetProcessName(pid);
                        var lowRiskShell = LowRiskShellProcesses.Contains(processName) && overlapArea < 200_000;
                        if (!lowRiskShell)
                        {
                            SafetyAudit.Record("privacy_block", "foreign_overlay", null);
                            throw new PrivacyBlockedException(
                                "別アプリのフローティング画面が操作対象に重なっているため、画像を外部送信しません。重なっている画面を閉じるか移動してから再実行してください。");
                        }
                    }
                }
            }

            var next = NativeMethods.GetWindow(current, NativeMethods.GwHwndPrev);
            if (next == current) break;
            current = next;
        }
    }

    public static void EnsureSafeToEgress(ScreenObservation observation)
    {
        var current = NativeMethods.GetForegroundWindow();
        if (current != observation.WindowHandle || current == nint.Zero)
            throw new ObservationChangedException("外部送信直前に前面画面が変化したため、取得済み情報を送信しません。");

        NativeMethods.GetWindowThreadProcessId(current, out var rawPid);
        if (checked((int)rawPid) != observation.ProcessId)
            throw new ObservationChangedException("外部送信直前に操作対象アプリが変化したため、取得済み情報を送信しません。");

        EnsureSafeToCapture(observation.ProcessName, observation.WindowTitle, observation.Controls);

        if (!NativeMethods.GetWindowRect(observation.WindowHandle, out var rect))
            throw new ObservationChangedException("外部送信直前に操作対象の位置を確認できなかったため送信しません。");

        EnsureNoForeignOverlay(observation.WindowHandle, observation.ProcessId, rect);
    }

    private static string GetProcessName(int pid)
    {
        try { return Process.GetProcessById(pid).ProcessName; }
        catch { return string.Empty; }
    }

    private static bool ContainsAny(string text, IEnumerable<string> terms)
        => terms.Any(term => text.Contains(term, StringComparison.OrdinalIgnoreCase));
}

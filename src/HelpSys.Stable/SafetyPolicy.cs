namespace HelpSys.Stable;

internal enum SafetyDisposition
{
    Allow,
    RequireHumanConfirmation,
    Block
}

internal sealed record SafetyPolicyDecision(
    SafetyDisposition Disposition,
    string Category,
    string Message);

internal static class SafetyPolicy
{
    private static readonly (string Category, string[] Terms)[] HardBlocks =
    [
        ("authentication", ["password", "passcode", "one-time", "otp", "verification code", "認証コード", "確認コード", "パスワード"]),
        ("administrative_privilege", ["administrator", "run as administrator", "elevation", "uac", "管理者として", "管理者権限"])
    ];

    private static readonly (string Category, string[] Terms)[] Confirmations =
    [
        ("payment", ["place order", "purchase", "pay now", "checkout", "購入", "注文を確定", "支払", "決済"]),
        ("destructive_delete", ["delete permanently", "empty recycle bin", "factory reset", "format", "完全に削除", "完全削除", "ごみ箱を空", "初期化", "フォーマット"]),
        ("external_send", ["send", "publish", "post", "share", "送信", "公開", "投稿", "共有"]),
        ("software_change", ["install", "uninstall", "インストール", "アンインストール"]),
        ("permission_change", ["allow access", "grant permission", "change permission", "アクセスを許可", "権限を許可", "権限を変更"]),
        ("agreement", ["accept terms", "agree and continue", "sign contract", "規約に同意", "同意して続行", "契約を締結"])
    ];

    public static SafetyPolicyDecision Evaluate(ScreenObservation observation, PlanResult plan)
    {
        if (plan.Status != "target")
            return new(SafetyDisposition.Allow, "none", string.Empty);

        var target = string.IsNullOrWhiteSpace(plan.TargetId)
            ? null
            : observation.Controls.FirstOrDefault(x => x.Id == plan.TargetId);

        if (target?.Password == true)
            return new(SafetyDisposition.Block, "authentication", "認証情報の入力操作はHelpSysから案内しません。");

        var signals = string.Join(' ', new[]
        {
            plan.Instruction ?? string.Empty,
            target?.Name ?? string.Empty,
            target?.AutomationId ?? string.Empty
        });

        foreach (var (category, terms) in HardBlocks)
        {
            if (ContainsAny(signals, terms))
                return new(SafetyDisposition.Block, category, "認証または管理者権限に関わる最終操作はHelpSysから案内しません。");
        }

        foreach (var (category, terms) in Confirmations)
        {
            if (ContainsAny(signals, terms))
                return new(
                    SafetyDisposition.RequireHumanConfirmation,
                    category,
                    "支払い・削除・外部送信・公開・インストール・権限変更などの重要操作に該当する可能性があります。案内を表示する前に本人確認が必要です。");
        }

        return new(SafetyDisposition.Allow, "low_risk", string.Empty);
    }

    private static bool ContainsAny(string text, IEnumerable<string> terms)
        => terms.Any(term => text.Contains(term, StringComparison.OrdinalIgnoreCase));
}

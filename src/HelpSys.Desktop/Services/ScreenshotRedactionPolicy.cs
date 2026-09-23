using System.Text.RegularExpressions;
using System.Windows;
using HelpSys.Models;

namespace HelpSys.Services;

public static class ScreenshotRedactionPolicy
{
    private static readonly string[] SensitiveInputTerms =
    [
        "password", "passwd", "passcode", "パスワード", "暗証番号", "pin", "otp", "totp", "2fa", "mfa",
        "one-time", "verification code", "security code", "認証コード", "確認コード", "ワンタイム",
        "api key", "apikey", "apiキー", "client secret", "secret key", "access token", "refresh token",
        "session token", "bearer token", "秘密鍵", "private key", "backup code", "recovery code",
        "cvv", "cvc", "card number", "カード番号"
    ];

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
    private static readonly Regex ApiKeyRegex = new(
        @"(?i)\b(?:sk-[A-Za-z0-9_-]{16,}|gh[pousr]_[A-Za-z0-9]{20,}|AIza[A-Za-z0-9_-]{20,}|AKIA[0-9A-Z]{16})\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex PrivateKeyRegex = new(
        @"-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex CardNumberRegex = new(
        @"(?<!\d)(?:\d[ -]?){13,19}(?!\d)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SensitiveUrlRegex = new(
        @"https?://[^\s<>""']*[?#][^\s<>""']+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static IReadOnlyList<Rect> Build(IReadOnlyList<UiElementCandidate> elements)
    {
        return elements
            .Where(ShouldRedact)
            .Select(x => x.Bounds)
            .Where(x => !x.IsEmpty)
            .Distinct()
            .ToArray();
    }

    private static bool ShouldRedact(UiElementCandidate element)
    {
        if (element.Password) return true;

        var hint = $"{element.Name} {element.AutomationId} {element.ClassName}";
        if (element.ControlType is "Edit" or "ComboBox" &&
            SensitiveInputTerms.Any(term => hint.Contains(term, StringComparison.OrdinalIgnoreCase)))
            return true;

        return ContainsSensitiveValue(element.Name) || ContainsSensitiveValue(element.Value);
    }

    private static bool ContainsSensitiveValue(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var value = text.Length <= 1000 ? text : text[..1000];

        if (EmailRegex.IsMatch(value) || JapanesePhoneRegex.IsMatch(value) || PostalCodeRegex.IsMatch(value) ||
            LabeledSecretRegex.IsMatch(value) || BearerRegex.IsMatch(value) || JwtRegex.IsMatch(value) ||
            ApiKeyRegex.IsMatch(value) || PrivateKeyRegex.IsMatch(value) || SensitiveUrlRegex.IsMatch(value))
            return true;

        foreach (Match match in CardNumberRegex.Matches(value))
        {
            var digits = new string(match.Value.Where(char.IsDigit).ToArray());
            if (digits.Length is >= 13 and <= 19 && PassesLuhn(digits)) return true;
        }

        return false;
    }

    private static bool PassesLuhn(string digits)
    {
        var sum = 0;
        var alternate = false;
        for (var index = digits.Length - 1; index >= 0; index--)
        {
            var number = digits[index] - '0';
            if (alternate)
            {
                number *= 2;
                if (number > 9) number -= 9;
            }
            sum += number;
            alternate = !alternate;
        }
        return sum % 10 == 0;
    }
}

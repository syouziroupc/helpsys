namespace HelpSys.Models;

public sealed record BrowserContextSnapshot(
    string ProcessName,
    string WindowTitle,
    string? Url,
    string? Domain,
    bool? Https,
    bool AddressFieldFocused)
{
    // The address bar may contain account-reset links, session identifiers, search terms or other
    // private path/query data. SystemContextService may need to inspect the current address briefly
    // to identify the site, but the cached snapshot must never retain the full HTTP(S) URL.
    public string? Url { get; init; } = MinimizeUrl(Url);

    private static string? MinimizeUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        if (text.Length > 900) text = text[..900];

        if (Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            if (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                // Origin only. UserInfo, path, query and fragment are deliberately discarded.
                var authority = uri.IsDefaultPort
                    ? $"{uri.Scheme}://{uri.Host}"
                    : $"{uri.Scheme}://{uri.Host}:{uri.Port}";
                return authority;
            }

            var scheme = uri.Scheme.ToLowerInvariant();
            if (scheme is "chrome" or "edge" or "about")
            {
                var local = text.ToLowerInvariant();
                if (ContainsAny(local, "password", "passkey", "login", "logins"))
                    return $"{scheme}:password-manager";
                if (ContainsAny(local, "cookie", "storage", "localstorage", "sessionstorage"))
                    return $"{scheme}:browser-storage";

                if (scheme == "about") return "about:internal";
                return string.IsNullOrWhiteSpace(uri.Host) ? $"{scheme}:internal" : $"{scheme}://{uri.Host}";
            }
        }

        // Keep a non-empty invalid marker so PrivacyGate classifies the state UNKNOWN instead of
        // silently treating an address it could not understand as safe.
        return "unparseable";
    }

    private static bool ContainsAny(string value, params string[] terms)
        => terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));
}

public sealed record SystemContextSnapshot(
    string ForegroundProcess,
    string ForegroundTitle,
    int ForegroundProcessId,
    bool TaskbarVisible,
    IReadOnlyList<string> RunningApps,
    BrowserContextSnapshot? Browser);
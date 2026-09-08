namespace HelpSys.Models;

public sealed record BrowserContextSnapshot(
    string ProcessName,
    string WindowTitle,
    string? Url,
    string? Domain,
    bool? Https,
    bool AddressFieldFocused);

public sealed record SystemContextSnapshot(
    string ForegroundProcess,
    string ForegroundTitle,
    int ForegroundProcessId,
    bool TaskbarVisible,
    IReadOnlyList<string> RunningApps,
    BrowserContextSnapshot? Browser);

using HelpSys.Models;

namespace HelpSys.Services;

internal sealed record UiAutomationObserverRequest(
    string Id,
    string Operation,
    int ProcessId = 0,
    int MaxCandidates = 0,
    UiElementCandidate? Candidate = null,
    int RootProcessId = 0,
    double X = 0,
    double Y = 0,
    double Width = 0,
    double Height = 0,
    long WindowHandle = 0,
    int ExpectedProcessId = 0);

internal sealed record UiAutomationObserverBounds(double X, double Y, double Width, double Height);

internal sealed record UiAutomationObserverResponse(
    string Id,
    bool Ok,
    string? Error = null,
    IReadOnlyList<UiElementCandidate>? Candidates = null,
    UiElementCandidate? Candidate = null,
    UiAutomationObserverBounds? Bounds = null,
    string? Diagnostics = null);

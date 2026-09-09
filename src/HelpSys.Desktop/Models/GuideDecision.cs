namespace HelpSys.Models;

public sealed record GuideDecision(
    string Status,
    string? TargetId,
    string Action,
    string Instruction,
    string? Question,
    string? Key,
    double Confidence,
    string? InputText = null);

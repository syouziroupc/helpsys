namespace HelpSys.Models;

public sealed record GuideDecision(
    string Status,
    string? TargetId,
    string Instruction,
    string? Question,
    double Confidence);

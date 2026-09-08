namespace HelpSys.Models;

public sealed record VisionGuideDecision(
    string Status,
    string? Label,
    string Instruction,
    string? Question,
    double X,
    double Y,
    double Width,
    double Height,
    double Confidence);

namespace HelpSys.Models;

public sealed record QualityGuideDecision(
    string Status,
    string? TargetId,
    string Action,
    string Instruction,
    string? Question,
    string? Key,
    double Confidence,
    double X,
    double Y,
    double Width,
    double Height,
    bool ScreenConfirmed,
    string VisualEvidence,
    string? CoordinateSpace = null,
    int CoordinateImageWidth = 0,
    int CoordinateImageHeight = 0,
    bool VisualConsensus = false);

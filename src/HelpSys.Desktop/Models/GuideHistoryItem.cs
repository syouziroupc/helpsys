namespace HelpSys.Models;

public sealed record GuideHistoryItem(
    int Step,
    string Action,
    string TargetName,
    string Instruction);

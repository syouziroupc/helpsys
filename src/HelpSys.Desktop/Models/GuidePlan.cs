namespace HelpSys.Models;

public sealed record GuidePlan(string Instruction, IReadOnlyList<string> TargetHints);

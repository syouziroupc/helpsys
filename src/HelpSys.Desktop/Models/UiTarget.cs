using System.Windows;

namespace HelpSys.Models;

public sealed record UiTarget(
    string Name,
    string AutomationId,
    string ControlType,
    Rect Bounds,
    int ProcessId,
    double Score);

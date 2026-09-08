using System.Text.Json.Serialization;
using System.Windows;

namespace HelpSys.Models;

public sealed record UiElementCandidate(
    string Id,
    string Name,
    string AutomationId,
    string ClassName,
    string ControlType,
    string ProcessName,
    bool Enabled,
    bool KeyboardFocusable,
    double X,
    double Y,
    double Width,
    double Height,
    int ProcessId)
{
    [JsonIgnore]
    public Rect Bounds => new(X, Y, Width, Height);
}

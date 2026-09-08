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
    bool Interactable,
    bool Enabled,
    bool KeyboardFocusable,
    bool Focused,
    bool Password,
    double X,
    double Y,
    double Width,
    double Height,
    int ProcessId,
    string? Value = null,
    string? ToggleState = null,
    bool? Selected = null,
    string? ExpandCollapseState = null)
{
    [JsonIgnore]
    public Rect Bounds => new(X, Y, Width, Height);
}

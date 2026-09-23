namespace HelpSys.Models;

public enum ActionEffectKind
{
    FocusTarget,
    ToggleOrSelection,
    SelectionOrExpansion,
    NavigationOrContentChange,
    TextSubmission
}

public sealed record ActionExpectation(
    ActionEffectKind Kind,
    string Action,
    UiElementCandidate? Target)
{
    public static ActionExpectation From(GuideDecision decision, UiElementCandidate? target)
    {
        var action = decision.Action.ToLowerInvariant();
        var type = target?.ControlType ?? string.Empty;

        if (action == "left_click" && type is "Edit" or "ComboBox")
            return new ActionExpectation(ActionEffectKind.FocusTarget, action, target);

        if (action == "left_click" && type is "CheckBox" or "RadioButton")
            return new ActionExpectation(ActionEffectKind.ToggleOrSelection, action, target);

        if (action == "left_click" && type is "TabItem" or "TreeItem" or "ListItem")
            return new ActionExpectation(ActionEffectKind.SelectionOrExpansion, action, target);

        if (action == "type_text")
            return new ActionExpectation(ActionEffectKind.TextSubmission, action, target);

        return new ActionExpectation(ActionEffectKind.NavigationOrContentChange, action, target);
    }
}

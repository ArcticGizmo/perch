namespace Perch.Data.Roost;

/// <summary>How a pane is drawn: a full thread, or a mini card (header + the last few activity lines).</summary>
public enum RoostPaneSize
{
    Collapsed = 0,
    Expanded = 1,
}

/// <summary>The Roost's three layouts (a persisted segmented toggle).</summary>
public enum RoostLayoutMode
{
    /// <summary>The <see cref="RoostStage"/>'s panes in a grid shaped by a <see cref="RoostSnapTemplate"/>.</summary>
    Tiled = 0,
    /// <summary>tmux main-vertical: the focused pane large and expanded, the rest stacked as mini cards.</summary>
    MainStack = 1,
    /// <summary>One pane, tmux <c>prefix z</c>.</summary>
    Zoom = 2,
}

/// <summary>The Main + stack and Zoom rules (UI-free, unit-tested). Tiled's are <see cref="RoostStage"/> and
/// <see cref="RoostTemplates"/>.</summary>
public static class RoostLayout
{
    /// <summary>
    /// Main + stack: the focused pane is the main one (falling back to the first pane when nothing visible is
    /// focused); every other pane stacks, in order. Null main for an empty roster.
    /// </summary>
    public static (string? Main, IReadOnlyList<string> Stack) MainStack(IReadOnlyList<string> keys, string? focused)
    {
        if (keys.Count == 0) return (null, []);
        var main = focused is not null && keys.Contains(focused) ? focused : keys[0];
        return (main, keys.Where(k => k != main).ToArray());
    }

    /// <summary>Zoom: the focused pane, else the first. Null for an empty roster.</summary>
    public static string? ZoomTarget(IReadOnlyList<string> keys, string? focused) =>
        focused is not null && keys.Contains(focused) ? focused : keys.Count > 0 ? keys[0] : null;
}

namespace Perch.Data.Roost;

/// <summary>What a Roost chord does.</summary>
public enum RoostCommand
{
    FocusTab,
    /// <summary>Tab N (1-based, the user's tabs).</summary>
    Tab,
    NextTab,
    PreviousTab,
    NewTab,
    RenameTab,
    /// <summary>Region N (1-based, reading order) of the active tab.</summary>
    Region,
    Zoom,
    NextNeedingYou,
}

/// <summary>A chord's modifiers.</summary>
[Flags]
public enum RoostMods
{
    None = 0,
    Ctrl = 1,
    Shift = 2,
    Alt = 4,
}

/// <summary>The cheat-sheet's sections.</summary>
public enum RoostKeyGroup
{
    Tabs,
    Regions,
    Sessions,
    View,
}

/// <summary>
/// One row of the Roost's shortcut table. <see cref="Keys"/> are key names as the UI reports them (Avalonia's
/// <c>Key.ToString()</c>: "D1", "Tab", "OemPeriod"…); for a ranged row ("Ctrl+1–9") key <c>i</c> resolves with
/// argument <c>i + 1</c>. A row with no <see cref="Command"/> is listed for the cheat-sheet only — a key a focused
/// pane handles itself (Enter / Esc on a permission card), or one the window handles outside this table (the zoom
/// chords, which the session window shares — see <c>ZoomHost</c>).
/// </summary>
public sealed record RoostKey(
    string Chord, string Description, RoostKeyGroup Group, RoostCommand? Command = null,
    RoostMods Mods = RoostMods.None, IReadOnlyList<string>? Keys = null, bool Hint = false);

/// <summary>
/// The Roost's keyboard shortcuts (D6) — the single table the window's key handler, the rail's cheat-sheet, the tab
/// tooltips and the bottom hint line all read, so they can't drift apart. UI-free, unit-tested.
/// </summary>
public static class RoostKeys
{
    private static string[] Digits(int from, int to) => Enumerable.Range(from, to - from + 1).Select(d => $"D{d}").ToArray();

    public static IReadOnlyList<RoostKey> All { get; } =
    [
        new("Ctrl+0", "Focus tab", RoostKeyGroup.Tabs, RoostCommand.FocusTab, RoostMods.Ctrl, ["D0"]),
        new("Ctrl+1–9", "Tab 1–9", RoostKeyGroup.Tabs, RoostCommand.Tab, RoostMods.Ctrl, Digits(1, 9), Hint: true),
        new("Ctrl+Tab", "Next tab", RoostKeyGroup.Tabs, RoostCommand.NextTab, RoostMods.Ctrl, ["Tab"]),
        new("Ctrl+Shift+Tab", "Previous tab", RoostKeyGroup.Tabs, RoostCommand.PreviousTab, RoostMods.Ctrl | RoostMods.Shift, ["Tab"]),
        new("Ctrl+T", "New tab", RoostKeyGroup.Tabs, RoostCommand.NewTab, RoostMods.Ctrl, ["T"]),
        new("F2", "Rename the tab", RoostKeyGroup.Tabs, RoostCommand.RenameTab, RoostMods.None, ["F2"]),
        new("Alt+1–8", "Region 1–8 of this tab", RoostKeyGroup.Regions, RoostCommand.Region, RoostMods.Alt, Digits(1, RoostGridLayout.MaxRegions), Hint: true),
        new("Ctrl+Shift+Z", "Zoom the focused pane (again to restore)", RoostKeyGroup.Regions, RoostCommand.Zoom, RoostMods.Ctrl | RoostMods.Shift, ["Z"]),
        new("Ctrl+.", "Next session needing you", RoostKeyGroup.Sessions, RoostCommand.NextNeedingYou, RoostMods.Ctrl, ["OemPeriod"], Hint: true),
        new("Enter", "Allow the focused pane's permission", RoostKeyGroup.Sessions),
        new("Esc", "Deny it — or interrupt a running turn", RoostKeyGroup.Sessions),
        new("Ctrl+= / Ctrl+−", "Bigger / smaller — the whole Roost (or Ctrl+wheel)", RoostKeyGroup.View),
    ];

    /// <summary>The command a key press runs, with its argument (a ranged row's 1-based index, else 0). Modifiers
    /// must match exactly. Null when no row binds it.</summary>
    public static (RoostCommand Command, int Arg)? Resolve(string key, RoostMods mods)
    {
        foreach (var row in All)
        {
            if (row.Command is not { } command || row.Keys is not { } keys || row.Mods != mods) continue;
            int at = IndexOf(keys, key);
            if (at >= 0) return (command, keys.Count > 1 ? at + 1 : 0);
        }
        return null;
    }

    /// <summary>The chord for a command (for a tooltip): the ranged row's chord with <paramref name="arg"/> filled
    /// in ("Ctrl+3"), else the row's own. Null when unbound.</summary>
    public static string? ChordFor(RoostCommand command, int arg = 0)
    {
        if (All.FirstOrDefault(r => r.Command == command) is not { } row) return null;
        if (row.Keys is not { Count: > 1 } || arg < 1 || arg > row.Keys.Count) return row.Chord;
        var prefix = row.Chord[..(row.Chord.LastIndexOf('+') + 1)];
        return prefix + arg;
    }

    /// <summary>The bottom bar's one-line summary of the hint rows.</summary>
    public static string HintLine => string.Join("  ·  ", All.Where(r => r.Hint).Select(r => $"{r.Chord} {ShortHint(r)}"));

    private static string ShortHint(RoostKey r) => r.Command switch
    {
        RoostCommand.Tab => "tab",
        RoostCommand.Region => "region",
        RoostCommand.NextNeedingYou => "next needing you",
        _ => r.Description.ToLowerInvariant(),
    };

    private static int IndexOf(IReadOnlyList<string> keys, string key)
    {
        for (int i = 0; i < keys.Count; i++) if (keys[i] == key) return i;
        return -1;
    }
}

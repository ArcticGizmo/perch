namespace Perch.Data.Hypertree;

/// <summary>
/// Perch's mirror of Hypertree's published status contract — the shape of
/// <c>%APPDATA%\hypertree\status.json</c>, which the Hypertree tray keeps current and explicitly offers
/// to outside readers (see <see cref="HypertreeStatusReader"/> for why we read the file rather than
/// shelling <c>htree list --json</c>).
/// </summary>
/// <remarks>
/// This is a copy of a contract owned by another repo, so it is deliberately a plain DTO with no
/// behaviour beyond what the overlay needs. <see cref="HypertreeStatusReader.SupportedSchema"/> guards
/// it: a schema we don't know is treated as "no Hypertree" rather than parsed hopefully.
/// <para>
/// Hypertree publishes the vertical stack already flattened top-to-bottom with the main timeline in its
/// slot, so <see cref="Rows"/> renders in array order and a reorder in Hypertree is simply a different
/// order here. Main is <em>not</em> a branch in Hypertree's model — it carries no id and is addressed by
/// the literal <c>main</c> — which is why <see cref="HypertreeRow.Id"/> is nullable.
/// </para>
/// </remarks>
internal sealed class HypertreeStatus
{
    /// <summary>Contract version. Only <see cref="HypertreeStatusReader.SupportedSchema"/> is accepted.</summary>
    public int Schema { get; set; }

    /// <summary>The running Hypertree's product version — shown on the Settings page.</summary>
    public string Version { get; set; } = "";

    /// <summary>The tray's process id, checked for liveness so a file left behind by a crash doesn't
    /// leave the overlay showing a stack that isn't there.</summary>
    public int Pid { get; set; }

    /// <summary>Absolute path to <c>htree.exe</c> beside the running tray, or null if it isn't there.
    /// Hypertree publishes this precisely so readers don't have to guess at an install layout.</summary>
    public string? Cli { get; set; }

    /// <summary>The vertical stack, top to bottom, main included at its slot.</summary>
    public List<HypertreeRow> Rows { get; set; } = new();

    /// <summary>Where the cursor actually is. Hypertree keeps this true even for desktop switches made
    /// outside it (Win+Ctrl+Arrow, Task View), so it's safe to drive the "you are here" marker from.</summary>
    public HypertreePosition Current { get; set; } = new();

    /// <summary>Whether <paramref name="index"/> is the row the cursor is on.</summary>
    public bool IsCurrentRow(int index) => index == Current.Row;
}

/// <summary>One row of the stack: the main timeline, or a branch.</summary>
internal sealed class HypertreeRow
{
    /// <summary><c>"main"</c> or <c>"branch"</c>.</summary>
    public string Kind { get; set; } = "branch";

    /// <summary>The branch's stable id — null for main. Jumps address this, never the list position, so a
    /// reorder between the read and the click can't land us on the wrong branch.</summary>
    public Guid? Id { get; set; }

    /// <summary>The branch's name, or <c>"main"</c>. Hypertree does not guarantee these are unique.</summary>
    public string Name { get; set; } = "";

    /// <summary>The row's resume point: the desktop index a jump to this row lands on.</summary>
    public int Cursor { get; set; }

    public List<HypertreeDesktop> Desktops { get; set; } = new();

    public bool IsMain => string.Equals(Kind, "main", StringComparison.OrdinalIgnoreCase);

    /// <summary>How this row is addressed on the <c>htree goto</c> command line: its id, or <c>main</c>.</summary>
    public string Target => IsMain ? "main" : Id?.ToString() ?? "main";

    /// <summary>The label of the desktop a jump would land on, or empty when the cursor doesn't resolve.</summary>
    public string ResumeLabel
        => Cursor >= 0 && Cursor < Desktops.Count ? Desktops[Cursor].Label : "";
}

/// <summary>One desktop within a row.</summary>
internal sealed class HypertreeDesktop
{
    /// <summary>The OS virtual-desktop GUID. Published for correlation; Perch addresses by position.</summary>
    public Guid Id { get; set; }

    public string Label { get; set; } = "";
}

/// <summary>Indices into <see cref="HypertreeStatus.Rows"/> and that row's desktops.</summary>
internal sealed class HypertreePosition
{
    public int Row { get; set; } = -1;
    public int Desktop { get; set; } = -1;
}

namespace Perch.Data.Work;

/// <summary>Where a work item is in its life. Only the user moves it (docs/work-items-plan.md D3): Perch never marks
/// anything done.</summary>
public enum WorkItemStatus
{
    /// <summary>Being worked on: shown in the overlay with its sessions.</summary>
    Active = 0,
    /// <summary>On hold: one dimmed, collapsed line in the overlay. Its sessions still alert.</summary>
    Paused = 1,
    /// <summary>Finished: gone from the overlay, kept (notes, links, log, sessions) in the Work items window.</summary>
    Done = 2,
}

/// <summary>How a session came to be in a work item, kept so an automatic join is visible and easy to undo.</summary>
public enum WorkJoin
{
    /// <summary>The user put it there (the row menu, the window, <c>perch work add</c>).</summary>
    Manual = 0,
    /// <summary>Started inside the item (its overlay "+", the launcher, <c>perch --work</c>).</summary>
    Launched = 1,
    /// <summary>A terminal <c>claude</c> started with <c>PERCH_WORK</c> set (the hook's <c>{sid}.work</c> marker).</summary>
    Terminal = 2,
}

/// <summary>A labelled link on a work item: a design doc, a PR, a ticket, anything with a URL (D4).</summary>
internal sealed class WorkLink
{
    public string Label { get; set; } = "";
    public string Url { get; set; } = "";
}

/// <summary>A session that belongs to a work item. <see cref="Title"/> and <see cref="Cwd"/> are snapshots taken while
/// the session was live, so an ended session can still be named and resumed from the item.</summary>
internal sealed class WorkMember
{
    public string SessionId { get; set; } = "";
    public WorkJoin Via { get; set; }
    public DateTime AddedUtc { get; set; } = DateTime.UtcNow;
    public string? Title { get; set; }
    public string? Cwd { get; set; }
}

/// <summary>
/// One line of a work item's log: either something the user wrote, or a fact Perch recorded (a session joined or
/// ended, the status changed). A line in the file that doesn't parse is kept as <see cref="Raw"/> and written back
/// verbatim, so a hand edit is never lost.
/// </summary>
internal sealed record WorkLogEntry(DateTimeOffset At, string Text, bool ByPerch = false, string? Raw = null)
{
    /// <summary>A line Perch couldn't read as an entry: shown as-is, never reformatted.</summary>
    public bool IsRaw => Raw is not null;

    public static WorkLogEntry Unparsed(string line) => new(default, line, false, line);
}

/// <summary>
/// A named piece of work that sessions belong to (docs/work-items-plan.md): a name plus whatever details the user
/// wants. It is as big as they like (D1), sessions can be in several (D2), and only the user finishes it (D3).
///
/// <para>Mutable, like <see cref="Todo"/>: it is edited in place and round-trips through <see cref="WorkItemFile"/>,
/// one markdown file per item. <see cref="FilePath"/> is where it was read from and is not part of the file.</para>
/// </summary>
internal sealed class WorkItem
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>A <see cref="WorkHues"/> name ("teal", "violet", …). Unknown names draw as the first hue.</summary>
    public string Colour { get; set; } = WorkHues.Names[0];
    public WorkItemStatus Status { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public List<WorkLink> Links { get; set; } = [];
    public List<WorkMember> Members { get; set; } = [];
    /// <summary>Free-form markdown: the body of the file above the log.</summary>
    public string Notes { get; set; } = "";
    /// <summary>Oldest first, as the file stores it (the log is append-only).</summary>
    public List<WorkLogEntry> Log { get; set; } = [];

    /// <summary>Front-matter lines Perch doesn't know (a key the user added by hand), written back unchanged.</summary>
    public List<string> ExtraFrontMatter { get; set; } = [];

    /// <summary>The file this item was read from (or written to). Not stored in the file.</summary>
    public string? FilePath { get; set; }

    public bool HasMember(string sessionId) => Members.Any(m => m.SessionId == sessionId);

    /// <summary>Adds the session unless it's already a member. Returns whether it was added.</summary>
    public bool AddMember(string sessionId, WorkJoin via, string? title, string? cwd, DateTime? nowUtc = null)
    {
        if (string.IsNullOrEmpty(sessionId) || HasMember(sessionId)) return false;
        Members.Add(new WorkMember
        {
            SessionId = sessionId, Via = via, AddedUtc = nowUtc ?? DateTime.UtcNow,
            Title = Blank(title), Cwd = Blank(cwd),
        });
        return true;
    }

    public bool RemoveMember(string sessionId) => Members.RemoveAll(m => m.SessionId == sessionId) > 0;

    /// <summary>Refreshes a member's title/cwd snapshot from the live session. Returns whether anything changed, so
    /// the caller writes the file only when it must.</summary>
    public bool RefreshMember(string sessionId, string? title, string? cwd)
    {
        if (Members.FirstOrDefault(m => m.SessionId == sessionId) is not { } m) return false;
        bool changed = false;
        if (Blank(title) is { } t && t != m.Title) { m.Title = t; changed = true; }
        if (Blank(cwd) is { } c && c != m.Cwd) { m.Cwd = c; changed = true; }
        return changed;
    }

    public void AddLog(string text, bool byPerch, DateTimeOffset? at = null)
    {
        var line = (text ?? "").ReplaceLineEndings(" ").Trim();
        if (line.Length > 0) Log.Add(new WorkLogEntry(at ?? DateTimeOffset.Now, line, byPerch));
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

/// <summary>The fixed set of work-item colours, by name. The RGB values live in
/// <see cref="Perch.Theming.FixedColors.WorkHues"/> (index-aligned) because they're identity labels that must read on
/// both light and dark, not theme roles.</summary>
internal static class WorkHues
{
    public static readonly string[] Names = ["teal", "violet", "amber", "sky", "pink", "lime", "indigo", "slate"];

    /// <summary>The index of a hue name, or 0 for an unknown one.</summary>
    public static int IndexOf(string? name)
    {
        int i = Array.FindIndex(Names, n => string.Equals(n, name?.Trim(), StringComparison.OrdinalIgnoreCase));
        return i < 0 ? 0 : i;
    }

    /// <summary>The first hue no item in <paramref name="inUse"/> has yet (cycling once they're all taken), so a new
    /// item stands apart from the ones already on screen.</summary>
    public static string NextFree(IEnumerable<string> inUse)
    {
        var used = inUse.Select(IndexOf).ToList();
        for (int i = 0; i < Names.Length; i++)
            if (!used.Contains(i)) return Names[i];
        return Names[used.Count % Names.Length];
    }
}

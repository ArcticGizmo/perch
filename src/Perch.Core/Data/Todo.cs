namespace Perch.Data;

/// <summary>
/// A user-authored to-do / reminder: the first bit of Perch state the <em>user</em> writes rather than
/// something derived from Claude's transcripts. A title, an optional free-text description, and an
/// optional due instant, plus the bookkeeping that keeps the overlay strip and the due-reminder honest.
///
/// <para>Mutable class (mirrors <see cref="QuickLink"/>) because it is edited in place in the Todo window
/// and round-trips through <see cref="TodoStore"/>'s JSON. All timestamps are stored in <b>UTC</b>;
/// the UI converts to local time only at the edges. <see cref="ReminderFiredUtc"/> records that the
/// due-reminder toast has already fired for this item, so a restart doesn't re-nag.</para>
/// </summary>
internal sealed class Todo
{
    /// <summary>Stable identity, assigned once on create. Used by the overlay's right-click Complete and
    /// by reminder de-duplication.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("n");

    public string Title { get; set; } = "";
    public string Description { get; set; } = "";

    /// <summary>When the item is due, in UTC. Null means "no due date" — it never becomes overdue and
    /// never fires a reminder; it simply sorts after dated items.</summary>
    public DateTime? DueUtc { get; set; }

    public bool Completed { get; set; }

    /// <summary>When it was marked complete (UTC). Null while outstanding.</summary>
    public DateTime? CompletedUtc { get; set; }

    /// <summary>When the due-reminder toast fired (UTC). Null until it has — this is what stops a
    /// once-due item from re-notifying on every poll and across restarts.</summary>
    public DateTime? ReminderFiredUtc { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public Todo Clone() => new()
    {
        Id = Id,
        Title = Title,
        Description = Description,
        DueUtc = DueUtc,
        Completed = Completed,
        CompletedUtc = CompletedUtc,
        ReminderFiredUtc = ReminderFiredUtc,
        CreatedUtc = CreatedUtc,
    };
}

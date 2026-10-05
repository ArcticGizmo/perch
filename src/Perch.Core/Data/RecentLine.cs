using Perch.Data.Roost;

namespace Perch.Data;

/// <summary>How a Recent line is coloured.</summary>
internal enum RecentTone
{
    /// <summary>An ordinary ending.</summary>
    Normal,
    /// <summary>Interrupted, or ended just before a shutdown: in the warning hue.</summary>
    Flagged,
    /// <summary>Perch had it open when Perch closed.</summary>
    Perch,
    /// <summary>Ended with <c>/exit</c> — the user meant it, so it's de-emphasised.</summary>
    Faded,
}

/// <summary>
/// One line of a Recent list — the overlay's Recent button and the Roost rail's Recent row both show these
/// (docs/session-recovery-plan.md). <paramref name="Folder"/> is the project folder's name when it differs from the title
/// (null otherwise); <paramref name="Note"/> the trailing label ("interrupted · 2h", "3h ago", "was open").
/// <paramref name="Interrupted"/> and <paramref name="BeforeShutdown"/> are the list's filters (a line a restart cut off
/// is both).
/// </summary>
internal readonly record struct RecentLine(
    string SessionId, string Cwd, string Title, string? Folder, string Note, RecentTone Tone,
    bool Interrupted, bool BeforeShutdown)
{
    /// <summary>Lights the Recent badge until it's been seen (<see cref="RecentSeen"/>): it was cut off, or ended just
    /// before a shutdown.</summary>
    public bool Badged => Interrupted || BeforeShutdown;

    /// <summary>The line for a session the Roost shows dormant: its /rename title (else its folder, with the folder beside a
    /// real title), a note that says how and when it ended, and the filters from its kind.</summary>
    public static RecentLine From(RoostDormant d, DateTime now)
    {
        var (note, tone) = d.Kind switch
        {
            RoostDormantKind.WasOpenInPerch => ("was open", RecentTone.Perch),
            RoostDormantKind.Interrupted => ($"interrupted · {RelativeTime.Span(now, d.LastActive)}", RecentTone.Flagged),
            RoostDormantKind.BeforeShutdown => ($"before shutdown · {RelativeTime.Span(now, d.LastActive)}", RecentTone.Flagged),
            RoostDormantKind.Exited => (RelativeTime.Ago(now, d.LastActive), RecentTone.Faded),
            RoostDormantKind.NotRunning => ("not running", RecentTone.Normal),
            _ => (RelativeTime.Ago(now, d.LastActive), RecentTone.Normal),
        };
        bool titled = !string.IsNullOrWhiteSpace(d.Title);
        return new(d.SessionId, d.Cwd, titled ? d.Title!.Trim() : d.ProjectName, titled ? d.ProjectName : null,
            note, tone, d.WasInterrupted, d.EndedBeforeShutdown);
    }
}

/// <summary>Which badged Recent lines the user has seen (opened the list while they were in it), so a Recent badge
/// lights only for new ones. Per surface and per run.</summary>
internal sealed class RecentSeen
{
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

    /// <summary>A badged line in <paramref name="lines"/> hasn't been seen yet.</summary>
    public bool HasUnseen(IEnumerable<RecentLine> lines)
    {
        foreach (var l in lines)
            if (l.Badged && !_seen.Contains(l.SessionId)) return true;
        return false;
    }

    /// <summary>The list was opened over <paramref name="lines"/>: their badged ones are seen.</summary>
    public void MarkSeen(IEnumerable<RecentLine> lines)
    {
        foreach (var l in lines)
            if (l.Badged) _seen.Add(l.SessionId);
    }
}

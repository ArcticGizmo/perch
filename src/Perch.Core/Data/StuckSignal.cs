namespace Perch.Data;

/// <summary>Which heuristic flagged a session as possibly stuck. Surfaced in the overlay's
/// warning-glyph tooltip so the user knows <em>why</em> Perch is worried.</summary>
public enum StuckKind
{
    /// <summary>Several tool calls in a row have failed (commands coming up empty / erroring).</summary>
    ErrorStreak = 0,
    /// <summary>The session keeps repeating the same action and it keeps failing.</summary>
    FailingLoop = 1,
}

/// <summary>
/// An advisory "this session may be stuck/spinning" signal derived from the transcript tail. Purely
/// informational — the session's <see cref="SessionStatus"/> stays <c>Running</c>; this just drives
/// the overlay's warning glyph. Null when nothing looks wrong (the common case).
/// </summary>
public sealed record StuckSignal(StuckKind Kind, string Reason);

/// <summary>
/// Raw, threshold-independent measurements of recent tool activity, read from the transcript tail by
/// <see cref="TranscriptReader.GetStuckMetrics"/>. The reader deliberately does not apply thresholds or
/// the on/off settings — <see cref="SessionMonitor"/> does — so the parse can stay memoised by mtime
/// while the user's sensitivity choices take effect immediately without invalidating the cache.
/// </summary>
public readonly record struct StuckMetrics(
    // Consecutive errored tool results ending at the most recent result (0 once any result succeeds),
    // so a session that erred then recovered is not flagged.
    int TrailingErrorStreak,
    // Within the last window of tool calls, how many times the single most-repeated call appears,
    // and how many of those repeats errored. A failing loop is high on both.
    int LoopRepeat,
    int LoopErrors,
    // A friendly description of the looping call ("Running: dotnet build"), for the tooltip.
    string? LoopLabel
);

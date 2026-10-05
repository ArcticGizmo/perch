using System.Text.Json.Nodes;

namespace Perch.Data;

/// <summary>How a session that is no longer running ended, as far as Claude Code's own records say. See
/// docs/session-recovery-plan.md (D1/D2) and the spike behind it.</summary>
internal enum SessionEndKind
{
    /// <summary>Nothing readable to go on (missing or empty transcript, or no parseable record in its tail).</summary>
    Unknown,
    /// <summary>A graceful exit with no <c>/exit</c>: Ctrl+C, Ctrl+D, closing the terminal tab or window, or
    /// Perch ending a session it drives.</summary>
    Closed,
    /// <summary>The user typed <c>/exit</c> (recorded in <c>history.jsonl</c>) after the session's last activity —
    /// the one deliberate "I'm done" signal Claude records.</summary>
    Exited,
    /// <summary>The process died without its exit flush: a restart/shutdown, a crash, or a hard kill.</summary>
    Abrupt,
}

/// <summary>A session's ending: its <see cref="Kind"/> and when it ended (local time), when that's known.
/// <see cref="HasConversation"/> is false when the transcript holds no user or assistant message — a session that
/// started and ended without a prompt, with nothing to resume.</summary>
internal readonly record struct SessionEnd(SessionEndKind Kind, DateTime? EndedAt, bool HasConversation = false)
{
    public static SessionEnd Unknown { get; } = new(SessionEndKind.Unknown, null);
}

/// <summary>
/// Classifies how a finished session ended, from its transcript's tail plus the last <c>/exit</c> the user typed in
/// it (<see cref="ExitCommandIndex"/>). The rule (docs/session-recovery-plan.md, D1/D2/D7) rests on one record:
/// Claude Code appends a <c>cost-state</c> line when it exits gracefully, and never during a live turn. So:
/// <list type="bullet">
/// <item>a <c>cost-state</c> after the last timestamped record → it exited cleanly (<see cref="SessionEndKind.Closed"/>,
/// or <see cref="SessionEndKind.Exited"/> when an <c>/exit</c> came after its last activity);</item>
/// <item>anything else → <see cref="SessionEndKind.Abrupt"/>, including a torn (half-written) last line.</item>
/// </list>
/// A <c>cost-state</c> mid-file just means it exited and was later resumed; only the tail decides. The caller only
/// asks about sessions that are no longer running — a live session's tail reads as "abrupt" too.
/// </summary>
internal static class SessionEndReader
{
    // The exit flush is a handful of small records, so a small tail normally holds it. The window only grows when
    // the tail is one huge record (a big tool result as the last thing before a kill), up to a cap.
    private const int TailBytes = 64 * 1024;
    private const int MaxTailBytes = 1024 * 1024;

    /// <summary>How the session whose transcript is <paramref name="path"/> ended. <paramref name="lastExitCommand"/>
    /// is the time of the last <c>/exit</c> typed in it, or null. Never throws.</summary>
    public static SessionEnd Read(string path, DateTime? lastExitCommand)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length == 0) return SessionEnd.Unknown;
            return Classify(ScanTail(path, file.Length), lastExitCommand, file.LastWriteTime);
        }
        catch
        {
            return SessionEnd.Unknown;
        }
    }

    /// <summary>The facts in the tail of the <paramref name="length"/>-byte transcript at <paramref name="path"/> — the
    /// file-reading half of <see cref="Read"/>, for a caller that caches it per (length, last write). Throws on IO.</summary>
    internal static TailFacts ScanTail(string path, long length)
    {
        // Grows until the tail holds a message: a transcript with none is small (bookkeeping only), so the window
        // reaches the whole file and that's settled too.
        for (int window = TailBytes; ; window *= 4)
        {
            var tail = Scan(TranscriptScan.ReadTailLines(path, window));
            if (tail.SawMessage || window >= MaxTailBytes || window >= length) return tail;
        }
    }

    /// <summary>What the tail says: whether any record parsed, whether any was a user or assistant message, whether it
    /// ends in the clean-exit flush, and the last activity time (the newest record carrying a <c>timestamp</c>).</summary>
    internal readonly record struct TailFacts(bool SawRecord, bool CleanExit, DateTime? LastActivity, bool SawMessage = false);

    internal static TailFacts Scan(IEnumerable<string> lines)
    {
        bool sawRecord = false, sawMessage = false, exitedAfterActivity = false, tornLast = false;
        DateTime? lastActivity = null;
        foreach (var raw in lines)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            JsonNode? record;
            try { record = JsonNode.Parse(raw); }
            catch { tornLast = true; continue; }   // only matters if nothing parses after it
            if (record is not JsonObject) { tornLast = true; continue; }

            sawRecord = true;
            tornLast = false;
            var type = TranscriptJson.AsString(record["type"]);
            if (type is "user" or "assistant") sawMessage = true;
            if (type == "cost-state")
            {
                exitedAfterActivity = true;
            }
            else if (TranscriptJson.ParseTimestamp(TranscriptJson.AsString(record["timestamp"])) is { } ts)
            {
                lastActivity = ts;
                exitedAfterActivity = false;   // activity after an exit flush = it was resumed since
            }
            // Untimestamped bookkeeping (last-prompt, mode, permission-mode…) changes nothing either way.
        }
        return new TailFacts(sawRecord, exitedAfterActivity && !tornLast, lastActivity, sawMessage);
    }

    /// <summary>Applies the D2 rule. A graceful exit's last write is the exit flush itself, so the file's last-write
    /// time is its exact end — <c>cost-state</c>'s own <c>startTime + totalDuration</c> can't be used, because a resume
    /// restores the original start time and keeps adding to the duration. An abrupt end is the last activity.</summary>
    internal static SessionEnd Classify(TailFacts tail, DateTime? lastExitCommand, DateTime lastWrite)
    {
        if (!tail.SawRecord) return SessionEnd.Unknown;

        // An /exit counts only if nothing happened after it: one typed before a later resume doesn't.
        bool exited = lastExitCommand is { } exit && (tail.LastActivity is not { } act || exit >= act);
        if (exited) return new SessionEnd(SessionEndKind.Exited, lastWrite, tail.SawMessage);
        if (tail.CleanExit) return new SessionEnd(SessionEndKind.Closed, lastWrite, tail.SawMessage);
        return new SessionEnd(SessionEndKind.Abrupt, tail.LastActivity ?? lastWrite, tail.SawMessage);
    }
}

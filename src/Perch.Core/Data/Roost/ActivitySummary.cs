using System.Text.Json.Nodes;
using Perch.Data.Control;

namespace Perch.Data.Roost;

/// <summary>What an <see cref="ActivityLine"/> describes — the mini card picks a glyph and hue per kind.</summary>
internal enum ActivityKind
{
    /// <summary>A tool call that finished (▸).</summary>
    Tool,
    /// <summary>A tool call still running (▸, with "running…").</summary>
    ToolRunning,
    /// <summary>A tool call that failed.</summary>
    ToolFailed,
    /// <summary>A permission prompt waiting on the user (⚠).</summary>
    Permission,
    /// <summary>An <c>AskUserQuestion</c> waiting on the user.</summary>
    Question,
    /// <summary>A plan waiting for approval.</summary>
    Plan,
    /// <summary>The turn's closing prose (✓ Done: …).</summary>
    Done,
    /// <summary>Prose mid-turn (or streaming).</summary>
    Prose,
    /// <summary>The user's own prompt.</summary>
    User,
    /// <summary>An error note (the process exited badly, a launch failure).</summary>
    Error,
    /// <summary>A <c>/compact</c> in progress.</summary>
    Compacting,
}

/// <summary>One line of a Roost mini card.</summary>
internal readonly record struct ActivityLine(ActivityKind Kind, string Text);

/// <summary>
/// The Roost mini card's body: the last few things a session did, newest last, from its conversation model
/// (UI-free, unit-tested). Walks the conversation backwards collecting at most <c>max</c> lines:
/// <list type="bullet">
/// <item>tool calls as their <see cref="ToolSummary"/> phrase plus the collapsed result where one reads better
///   ("Read 42 lines", via <see cref="ToolResultFormat"/>);</item>
/// <item>a pending permission / question / plan (always kept, as the newest line — it's why the card matters);</item>
/// <item>the turn's closing prose as "Done: …" once the turn is over, otherwise mid-turn prose as-is;</item>
/// <item>the user's prompt, error notes, and a running compaction.</item>
/// </list>
/// Thinking, resolved permissions (their tool card follows) and info notes are skipped.
/// </summary>
internal static class ActivitySummary
{
    public const int DefaultMax = 3;
    private const int MaxChars = 96;

    /// <param name="conv">The conversation (live Perch session or a tailed transcript).</param>
    /// <param name="sessionRunning">The session is mid-turn per the monitor. A tailed transcript never marks its
    /// assistant items complete, so closing prose only reads as "Done" when the session isn't running.</param>
    /// <param name="max">Lines to return at most.</param>
    public static IReadOnlyList<ActivityLine> Build(SessionConversation conv, bool sessionRunning, int max = DefaultMax)
    {
        var newestFirst = new List<ActivityLine>(max);
        bool turnOver = !sessionRunning && !conv.TurnActive;

        for (int i = conv.Items.Count - 1; i >= 0 && newestFirst.Count < max; i--)
        {
            bool newestItem = i == conv.Items.Count - 1;
            switch (conv.Items[i])
            {
                case PermissionItem { Resolution: PermissionResolution.Pending } p:
                    newestFirst.Add(Pending(p));
                    break;

                case AssistantMessageItem a:
                    for (int j = a.Parts.Count - 1; j >= 0 && newestFirst.Count < max; j--)
                    {
                        bool closing = newestItem && j == a.Parts.Count - 1;
                        if (Line(a.Parts[j], closing && (a.IsComplete || turnOver)) is { } line) newestFirst.Add(line);
                    }
                    break;

                case UserMessageItem u when Snippet(u.Text) is { Length: > 0 } text:
                    newestFirst.Add(new ActivityLine(ActivityKind.User, "You: " + text));
                    break;

                case NoteItem { Kind: NoteKind.Error } n when Snippet(n.Text) is { Length: > 0 } text:
                    newestFirst.Add(new ActivityLine(ActivityKind.Error, text));
                    break;

                case CompactionItem { IsDone: false } c:
                    newestFirst.Add(new ActivityLine(ActivityKind.Compacting,
                        c.Percent is { } pct ? $"Compacting conversation… {pct}%" : "Compacting conversation…"));
                    break;
            }
        }

        newestFirst.Reverse();
        return newestFirst;
    }

    private static ActivityLine? Line(AssistantPart part, bool closingProse) => part switch
    {
        ToolCallPart t => Tool(t),
        TextPart { Text: var text } when Snippet(text) is { Length: > 0 } s =>
            closingProse ? new ActivityLine(ActivityKind.Done, "Done: " + s) : new ActivityLine(ActivityKind.Prose, s),
        _ => null,
    };

    private static ActivityLine Tool(ToolCallPart t)
    {
        var phrase = Clip(string.IsNullOrWhiteSpace(t.Summary) ? t.ToolName : t.Summary.Trim());
        switch (t.Status)
        {
            case ToolCallStatus.Running:
                return new ActivityLine(ActivityKind.ToolRunning, phrase + " (running…)");
            case ToolCallStatus.Failed:
                return new ActivityLine(ActivityKind.ToolFailed, phrase + " (failed)");
            default:
                JsonNode? input = null;
                try { input = JsonNode.Parse(t.InputJson); } catch { /* summary only */ }
                return ToolResultFormat.CollapsedSummary(t.ToolName, input, t.ResultText) is { } result
                    ? new ActivityLine(ActivityKind.Tool, Clip($"{phrase} · {result}"))
                    : new ActivityLine(ActivityKind.Tool, phrase);
        }
    }

    private static ActivityLine Pending(PermissionItem p)
    {
        if (p.IsQuestion)
        {
            var q = AskUserQuestionInput.Parse(p.Request.InputJson);
            return new ActivityLine(ActivityKind.Question,
                q.Count > 0 ? Clip("Asks: " + q[0].Question) : "Has a question for you");
        }
        if (p.IsPlan) return new ActivityLine(ActivityKind.Plan, "Plan ready for your approval");
        var what = Snippet(p.Request.Description);
        return new ActivityLine(ActivityKind.Permission,
            Clip(string.IsNullOrEmpty(what) ? $"Allow {p.Request.ToolName}?" : $"Allow {p.Request.ToolName}: {what}?"));
    }

    /// <summary>The first meaningful line of some prose, Markdown decoration stripped and clipped.</summary>
    internal static string Snippet(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("```", StringComparison.Ordinal)) continue;   // a fence opener says nothing
            line = line.TrimStart('#', '>', '*', '-', ' ').Replace("**", "").Replace("`", "").Trim();
            if (line.Length > 0) return Clip(line);
        }
        return "";
    }

    private static string Clip(string s) => s.Length <= MaxChars ? s : s[..(MaxChars - 1)].TrimEnd() + "…";
}

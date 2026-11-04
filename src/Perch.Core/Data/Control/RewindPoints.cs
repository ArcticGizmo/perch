using System.Text.Json.Nodes;

namespace Perch.Data.Control;

/// <summary>A genuine user prompt on a transcript's active chain: the anchor a rewind or fork names.
/// <paramref name="ParentUuid"/> is the entry to keep when rewinding to <em>before</em> this prompt; null for a
/// conversation's first prompt.</summary>
internal sealed record PromptRef(string Uuid, string? ParentUuid, string Text);

/// <summary>
/// Finds where in the transcript each prompt of a live thread sits. A prompt Perch sent has no uuid on the wire
/// (the CLI only echoes user messages with <c>--replay-user-messages</c>), so the anchor comes from the transcript
/// the CLI wrote: its active-chain prompts are paired with the thread's prompts by text, newest first.
/// </summary>
internal static class RewindPoints
{
    /// <summary>The genuine prompts on the active chain (<see cref="TranscriptChain"/>), oldest first. A prompt is
    /// what <see cref="SessionConversation.GenuineUserPrompt"/> accepts: no tool results, meta records or
    /// slash-command echoes. Never throws.</summary>
    public static List<PromptRef> Read(IEnumerable<string> activeLines)
    {
        var prompts = new List<PromptRef>();
        foreach (var line in activeLines)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(line) || JsonNode.Parse(line) is not { } root) continue;
                if (TranscriptJson.AsString(root["type"]) != "user" || root["isSidechain"]?.GetValue<bool>() == true) continue;
                if (TranscriptJson.AsString(root["uuid"]) is not { Length: > 0 } uuid) continue;
                if (SessionConversation.GenuineUserPrompt(root) is not { } text) continue;
                var parent = TranscriptJson.AsString(root["parentUuid"]);
                prompts.Add(new PromptRef(uuid, string.IsNullOrEmpty(parent) ? null : parent, text));
            }
            catch { /* a malformed line is skipped */ }
        }
        return prompts;
    }

    /// <summary>
    /// Pairs each thread prompt (oldest first) with its transcript prompt, or null when it can't be placed.
    /// Walks both newest first and pairs only on equal text, so a transcript prompt the thread never showed (one
    /// sent from Remote Control) or a thread prompt the transcript holds as something else (a slash command, a
    /// prompt queued mid-turn) is stepped over rather than shifting every pairing after it.
    /// </summary>
    public static PromptRef?[] Match(IReadOnlyList<string> threadPrompts, IReadOnlyList<PromptRef> transcript)
    {
        var result = new PromptRef?[threadPrompts.Count];
        int j = transcript.Count - 1;
        for (int i = threadPrompts.Count - 1; i >= 0 && j >= 0; i--)
        {
            var want = Normalize(threadPrompts[i]);
            for (int k = j; k >= 0; k--)
            {
                if (Normalize(transcript[k].Text) != want) continue;
                result[i] = transcript[k];
                j = k - 1;
                break;
            }
        }
        return result;
    }

    private static string Normalize(string s) => s.Replace("\r\n", "\n").Trim();
}

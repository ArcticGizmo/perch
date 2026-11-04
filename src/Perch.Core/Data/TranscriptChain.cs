using System.Text;
using System.Text.Json;

namespace Perch.Data;

/// <summary>
/// A transcript is an append-only <em>tree</em>: each record names its <c>parentUuid</c>, and a rewind (the TUI's
/// Esc-Esc, or Perch's <c>--resume-session-at</c>) makes the next record parent onto an earlier one, leaving the
/// dropped turns in the file on a dead branch. Reading the file top to bottom would show those turns as if they
/// still happened, so readers that rebuild a conversation keep only the <b>active chain</b>: the leaf's ancestors.
///
/// <para>Best-effort and never lossy on a damaged file. Records without a <c>uuid</c> (titles, modes, snapshots)
/// and sub-agent records are always kept. The walk hops a compaction boundary through its
/// <c>logicalParentUuid</c>. Everything in the file before the point where the walk stops (a root, a missing
/// parent from a clipped or broken file) is kept in file order, so only branches the walk can prove dead are
/// dropped.</para>
/// </summary>
internal static class TranscriptChain
{
    /// <summary>The tree fields of one record. All null for a blank or unreadable line.</summary>
    internal readonly record struct Link(string? Uuid, string? Parent, string? LogicalParent, bool Sidechain);

    /// <summary>Reads only the top-level tree fields, skipping nested values unparsed. Never throws.</summary>
    public static Link ReadLink(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return default;
        string? uuid = null, parent = null, logical = null;
        bool sidechain = false;
        try
        {
            var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(line));
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return default;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var name = reader.GetString();
                if (!reader.Read()) break;
                switch (reader.TokenType)
                {
                    case JsonTokenType.String:
                        if (name == "uuid") uuid = reader.GetString();
                        else if (name == "parentUuid") parent = reader.GetString();
                        else if (name == "logicalParentUuid") logical = reader.GetString();
                        break;
                    case JsonTokenType.True when name == "isSidechain":
                        sidechain = true;
                        break;
                    case JsonTokenType.StartObject or JsonTokenType.StartArray:
                        reader.Skip();
                        break;
                }
            }
        }
        catch (JsonException)
        {
            // A partial trailing line: whatever was read before the break still stands.
        }
        return new Link(NullIfEmpty(uuid), NullIfEmpty(parent), NullIfEmpty(logical), sidechain);
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    /// <summary>
    /// Which lines to keep, by index. <paramref name="leafUuid"/> picks the branch: a rewind that hasn't been
    /// followed by a new record yet has its kept entry somewhere mid-file, so the caller names it. Null (or not
    /// found) means the last main-chain record in the file, which is how the CLI itself resumes.
    /// </summary>
    public static bool[] KeepMask(IReadOnlyList<Link> links, string? leafUuid = null)
    {
        var keep = new bool[links.Count];
        var indexOf = new Dictionary<string, int>(StringComparer.Ordinal);
        int lastMain = -1;
        for (int i = 0; i < links.Count; i++)
        {
            var l = links[i];
            if (l.Uuid is null || l.Sidechain)
            {
                keep[i] = true;
                continue;
            }
            indexOf.TryAdd(l.Uuid, i);
            lastMain = i;
        }
        if (lastMain < 0) return keep;   // nothing on a chain: every line is already kept

        int cur = leafUuid is { } leaf && indexOf.TryGetValue(leaf, out var at) ? at : lastMain;
        int root = cur;
        var seen = new HashSet<int>();
        while (seen.Add(cur))
        {
            keep[cur] = true;
            root = Math.Min(root, cur);
            var l = links[cur];
            var next = l.Parent ?? l.LogicalParent;
            if (next is null || !indexOf.TryGetValue(next, out var p)) break;
            cur = p;
        }
        for (int i = 0; i < root; i++) keep[i] = true;
        return keep;
    }

    /// <summary>The lines of the active chain, in file order (see <see cref="KeepMask"/>).</summary>
    public static List<string> ActiveLines(IReadOnlyList<string> lines, string? leafUuid = null)
    {
        var mask = KeepMask(lines.Select(ReadLink).ToList(), leafUuid);
        var kept = new List<string>(lines.Count);
        for (int i = 0; i < lines.Count; i++)
            if (mask[i]) kept.Add(lines[i]);
        return kept;
    }

    /// <summary>
    /// <see cref="ActiveLines"/> without holding the file: one pass reads only each record's tree fields, the
    /// second yields the kept lines. Lines appended between the passes (a live session) are kept.
    /// </summary>
    public static IEnumerable<string> ActiveLines(Func<IEnumerable<string>> readLines, string? leafUuid = null)
    {
        var mask = KeepMask(readLines().Select(ReadLink).ToList(), leafUuid);
        int i = 0;
        foreach (var line in readLines())
        {
            if (i >= mask.Length || mask[i]) yield return line;
            i++;
        }
    }
}

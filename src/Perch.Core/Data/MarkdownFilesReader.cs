using System.Text.Json.Nodes;

namespace Perch.Data;

/// <summary>
/// The Markdown files a session touched, split by how it touched them: those it <em>produced</em>
/// (wrote or edited) and those it only <em>referenced</em> (read). Paths are as recorded in the
/// transcript — absolute, from whatever OS wrote them — de-duplicated and in first-seen order. A file
/// touched both ways is listed only under <see cref="Produced"/>. See <see cref="MarkdownFilesReader"/>.
/// </summary>
public sealed record MarkdownFileSets(
    IReadOnlyList<string> Produced,
    IReadOnlyList<string> Referenced)
{
    /// <summary>The no-files result (transcript missing/unreadable, or the session touched no Markdown).</summary>
    public static readonly MarkdownFileSets Empty = new([], []);

    /// <summary>True when the session neither produced nor referenced any Markdown.</summary>
    public bool IsEmpty => Produced.Count == 0 && Referenced.Count == 0;
}

/// <summary>
/// Reconstructs which Markdown (<c>.md</c>/<c>.markdown</c>) files a session produced or referenced by
/// replaying its file-touching tool calls from the transcript. <c>Write</c>/<c>Edit</c>/<c>MultiEdit</c>/
/// <c>NotebookEdit</c> are <em>produce</em> tools (their <c>file_path</c>/<c>notebook_path</c> is a file
/// the session created or changed); <c>Read</c> is a <em>reference</em>. <c>Grep</c>/<c>Glob</c> target
/// glob/regex patterns rather than a specific file, so they contribute nothing.
///
/// A produce whose paired <c>tool_result</c> came back <c>is_error</c> (the write failed) is dropped, so a
/// failed edit never counts as a produced file. A file both produced and read surfaces only as produced.
///
/// Like the other transcript readers this is best-effort and must never throw: any failure yields
/// <see cref="MarkdownFileSets.Empty"/>. The result is memoised per transcript by (length, last-write) via
/// <see cref="MtimeCache{T}"/>, so a scan over an unchanged transcript costs a stat, not a parse. The
/// glyph's cheap <see cref="ProducedAnyMarkdown"/> reads the same cached sets — the whole-file walk only
/// re-runs when the transcript actually changed.
/// </summary>
internal sealed class MarkdownFilesReader
{
    private readonly MtimeCache<MarkdownFileSets> _sets = new();

    /// <summary>
    /// The session's produced/referenced Markdown file sets, or <see cref="MarkdownFileSets.Empty"/> when
    /// the transcript can't be located/read or it touched no Markdown. Best-effort; never throws.
    /// </summary>
    public MarkdownFileSets GetFileSets(string sessionId, string cwd)
    {
        if (string.IsNullOrEmpty(sessionId))
            return MarkdownFileSets.Empty;
        var path = TranscriptLocator.Resolve(sessionId, cwd);
        return path == null ? MarkdownFileSets.Empty : _sets.GetOrCompute(path, ParseSets, MarkdownFileSets.Empty);
    }

    /// <summary>
    /// True when the session produced (wrote/edited) at least one Markdown file — the signal behind the
    /// overlay glyph. Merely reading a <c>.md</c> (CLAUDE.md, README) does not count, by design: almost
    /// every session reads one. Reads the same mtime-cached sets as <see cref="GetFileSets"/>.
    /// </summary>
    public bool ProducedAnyMarkdown(string sessionId, string cwd) =>
        GetFileSets(sessionId, cwd).Produced.Count > 0;

    private static MarkdownFileSets ParseSets(string path)
    {
        // tool_use blocks live in assistant records; the paired tool_result (with its pass/fail flag)
        // lives in the following user record, keyed by tool_use_id. Walk the whole file chronologically —
        // a file can be touched anywhere in the session — collecting every qualifying .md tool call by id
        // and every errored result id, then reconcile at the end. It's cheap: the substring pre-filter
        // skips almost every line, and the result is cached by length+mtime.
        var touches = new List<(string Id, string Path, bool Produced)>();
        var errored = new HashSet<string>();

        foreach (var line in TranscriptScan.ReadLines(path))
        {
            // Cheap pre-filter: a produce/read call carries the ".md" path text; an errored result carries
            // the "is_error" flag we need to discard a failed write. (A successful result usually omits
            // is_error entirely, so it's simply absent from the errored set — which is correct.)
            bool maybeTouch = line.Contains("tool_use") && line.Contains(".md");
            bool maybeError = line.Contains("is_error");
            if (!maybeTouch && !maybeError)
                continue;

            try
            {
                if (TranscriptJson.ContentArray(JsonNode.Parse(line)) is not { } content)
                    continue;

                foreach (var block in content)
                {
                    var type = TranscriptJson.BlockType(block);
                    if (type == "tool_use")
                    {
                        var name = block!["name"]?.GetValue<string>();
                        var id = block["id"]?.GetValue<string>();
                        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(id))
                            continue;
                        if (MarkdownTool(name) is not { } kind)
                            continue;
                        var (pathKey, produced) = kind;
                        var file = block["input"]?[pathKey]?.GetValue<string>();
                        if (!IsMarkdown(file))
                            continue;
                        touches.Add((id, file!, produced));
                    }
                    else if (type == "tool_result")
                    {
                        if (block!["is_error"]?.GetValue<bool>() == true
                            && block["tool_use_id"]?.GetValue<string>() is { } rid)
                            errored.Add(rid);
                    }
                }
            }
            catch
            {
                // Malformed/partial line (transcripts are appended live) — skip it.
            }
        }

        if (touches.Count == 0)
            return MarkdownFileSets.Empty;

        // Reconcile: drop failed produces, then aggregate by a canonical path key (separators unified,
        // case-insensitive — transcripts carry paths from whatever OS wrote them) with produce taking
        // precedence over reference, preserving first-seen order.
        var order = new List<string>();                                             // canonical keys, first-seen order
        var producedByKey = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var displayByKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (id, file, produced) in touches)
        {
            if (errored.Contains(id))
                continue;
            var key = file.Replace('\\', '/');
            if (!displayByKey.ContainsKey(key))
            {
                order.Add(key);
                displayByKey[key] = file;
                producedByKey[key] = produced;
            }
            else if (produced && !producedByKey[key])
            {
                producedByKey[key] = true;   // a later produce promotes a file first seen as a reference
            }
        }

        var producedList = order.Where(k => producedByKey[k]).Select(k => displayByKey[k]).ToList();
        var referencedList = order.Where(k => !producedByKey[k]).Select(k => displayByKey[k]).ToList();
        return new MarkdownFileSets(producedList, referencedList);
    }

    // The path parameter to read off a file-touching tool's input, and whether the tool produces the file
    // (write/edit) or merely references it (read). Null for tools that don't target a concrete file —
    // Grep/Glob take patterns, Bash/Task/etc. are irrelevant here.
    private static (string PathKey, bool Produced)? MarkdownTool(string name) => name switch
    {
        "Write" or "Edit" or "MultiEdit" => ("file_path", true),
        "NotebookEdit"                    => ("notebook_path", true),   // .ipynb in practice; filtered by extension
        "Read"                            => ("file_path", false),
        _                                 => null,
    };

    private static bool IsMarkdown(string? path) =>
        !string.IsNullOrWhiteSpace(path)
        && (path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase));
}

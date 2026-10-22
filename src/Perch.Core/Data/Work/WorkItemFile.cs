using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Perch.Data.Work;

/// <summary>
/// Reads and writes one work item as a markdown file (docs/work-items-plan.md D6): a front-matter block holding the
/// structure, then the notes, then an append-only <c>## Log</c>.
///
/// <para>The front matter is a small fixed YAML subset that Perch writes itself: <c>key: value</c> scalars and lists
/// of maps (<c>  - key: value</c> / <c>    key: value</c>). Values Perch writes are JSON-quoted when they need to be,
/// and bare, double-quoted or single-quoted values all read back. There's no YAML dependency. Keys Perch doesn't know
/// are kept and written back unchanged, and a log line that doesn't parse is kept verbatim, so a hand edit is never
/// lost. A file with no front matter at all reads as an item named after the file, its whole text the notes.</para>
///
/// <para><see cref="Parse"/> returns null for a file whose front matter starts but never ends, or has no name and no
/// fallback: the store then leaves that file alone rather than rewrite something it couldn't read.</para>
/// </summary>
internal static class WorkItemFile
{
    public const string LogHeading = "## Log";
    private const string PerchTag = "(perch) ";
    private const string LogTimeFormat = "yyyy-MM-dd HH:mm zzz";

    // ── Reading ──

    /// <summary>Parses a work-item file. <paramref name="fallbackId"/> (the file stem) stands in for a missing id and
    /// name. Never throws.</summary>
    public static WorkItem? Parse(string text, string fallbackId)
    {
        try
        {
            var lines = (text ?? "").ReplaceLineEndings("\n").Split('\n');
            int bodyStart = 0;
            var item = new WorkItem();

            if (lines.Length > 0 && lines[0].TrimEnd() == "---")
            {
                int end = Array.FindIndex(lines, 1, l => l.TrimEnd() == "---");
                if (end < 0) return null;   // an unterminated front matter: don't guess
                ReadFrontMatter(lines.AsSpan(1, end - 1), item);
                bodyStart = end + 1;
            }

            if (string.IsNullOrWhiteSpace(item.Id)) item.Id = fallbackId;
            if (string.IsNullOrWhiteSpace(item.Name)) item.Name = fallbackId;
            if (string.IsNullOrWhiteSpace(item.Id)) return null;

            ReadBody(lines.AsSpan(bodyStart), item);
            return item;
        }
        catch
        {
            return null;
        }
    }

    private static void ReadFrontMatter(ReadOnlySpan<string> lines, WorkItem item)
    {
        string? listKey = null;                       // the list we're inside (links / sessions)
        Dictionary<string, string>? map = null;       // the list entry being filled
        List<string>? extra = null;                   // an unknown key's lines, kept verbatim

        void FlushMap()
        {
            if (map is null || listKey is null) { map = null; return; }
            if (listKey == "links")
            {
                var url = map.GetValueOrDefault("url") ?? "";
                if (url.Length > 0)
                    item.Links.Add(new WorkLink { Label = map.GetValueOrDefault("label") ?? "", Url = url });
            }
            else if (listKey == "sessions")
            {
                var id = map.GetValueOrDefault("id") ?? "";
                if (id.Length > 0 && !item.HasMember(id))
                    item.Members.Add(new WorkMember
                    {
                        SessionId = id,
                        Via = ParseVia(map.GetValueOrDefault("via")),
                        AddedUtc = ParseUtc(map.GetValueOrDefault("added")) ?? item.CreatedUtc,
                        Title = NullIfBlank(map.GetValueOrDefault("title")),
                        Cwd = NullIfBlank(map.GetValueOrDefault("cwd")),
                    });
            }
            map = null;
        }

        foreach (var raw in lines)
        {
            if (raw.Trim().Length == 0 || raw.TrimStart().StartsWith('#'))
            {
                extra?.Add(raw);
                continue;
            }
            bool indented = raw[0] is ' ' or '\t';

            if (!indented)
            {
                FlushMap();
                listKey = null;
                extra = null;
                if (!SplitKey(raw, out var key, out var value)) continue;
                switch (key)
                {
                    case "id": item.Id = Unquote(value); break;
                    case "name": item.Name = Unquote(value); break;
                    case "colour" or "color": item.Colour = Unquote(value); break;
                    case "status": item.Status = ParseStatus(Unquote(value)); break;
                    case "created": item.CreatedUtc = ParseUtc(Unquote(value)) ?? item.CreatedUtc; break;
                    case "links" or "sessions": listKey = key; break;
                    default:
                        extra = item.ExtraFrontMatter;
                        extra.Add(raw);
                        break;
                }
                continue;
            }

            if (extra is not null) { extra.Add(raw); continue; }
            if (listKey is null) continue;

            var t = raw.TrimStart();
            if (t.StartsWith("- ") || t == "-")
            {
                FlushMap();
                map = new Dictionary<string, string>(StringComparer.Ordinal);
                t = t.Length > 1 ? t[2..].TrimStart() : "";
                if (t.Length == 0) continue;
            }
            if (map is not null && SplitKey(t, out var k, out var v)) map[k] = Unquote(v);
        }
        FlushMap();
    }

    private static void ReadBody(ReadOnlySpan<string> lines, WorkItem item)
    {
        int log = -1;
        for (int i = lines.Length - 1; i >= 0; i--)
            if (lines[i].TrimEnd() == LogHeading) { log = i; break; }

        var notes = (log < 0 ? lines : lines[..log]).ToArray();
        item.Notes = string.Join("\n", notes).Trim('\n').TrimEnd();

        if (log < 0) return;
        foreach (var raw in lines[(log + 1)..])
        {
            if (raw.Trim().Length == 0) continue;
            item.Log.Add(ParseLogLine(raw));
        }
    }

    internal static WorkLogEntry ParseLogLine(string raw)
    {
        var t = raw.Trim();
        if (!t.StartsWith("- ")) return WorkLogEntry.Unparsed(raw);
        var rest = t[2..];
        // "yyyy-MM-dd HH:mm +hh:mm" is 23 characters.
        if (rest.Length < 23 ||
            !DateTimeOffset.TryParseExact(rest[..23], LogTimeFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var at))
            return WorkLogEntry.Unparsed(raw);
        var text = rest[23..].Trim();
        bool byPerch = text.StartsWith(PerchTag.TrimEnd(), StringComparison.Ordinal);
        if (byPerch) text = text[PerchTag.TrimEnd().Length..].Trim();
        return text.Length == 0 ? WorkLogEntry.Unparsed(raw) : new WorkLogEntry(at, text, byPerch);
    }

    // ── Writing ──

    /// <summary>The file text for <paramref name="item"/>. Round-trips through <see cref="Parse"/>.</summary>
    public static string Serialize(WorkItem item)
    {
        var sb = new StringBuilder();
        sb.Append("---\n");
        sb.Append("id: ").Append(Scalar(item.Id)).Append('\n');
        sb.Append("name: ").Append(Quote(item.Name)).Append('\n');
        sb.Append("colour: ").Append(Scalar(item.Colour)).Append('\n');
        sb.Append("status: ").Append(item.Status.ToString().ToLowerInvariant()).Append('\n');
        sb.Append("created: ").Append(FormatUtc(item.CreatedUtc)).Append('\n');

        if (item.Links.Count == 0) sb.Append("links: []\n");
        else
        {
            sb.Append("links:\n");
            foreach (var l in item.Links)
            {
                sb.Append("  - label: ").Append(Quote(l.Label)).Append('\n');
                sb.Append("    url: ").Append(Quote(l.Url)).Append('\n');
            }
        }

        if (item.Members.Count == 0) sb.Append("sessions: []\n");
        else
        {
            sb.Append("sessions:\n");
            foreach (var m in item.Members)
            {
                sb.Append("  - id: ").Append(Scalar(m.SessionId)).Append('\n');
                sb.Append("    via: ").Append(m.Via.ToString().ToLowerInvariant()).Append('\n');
                sb.Append("    added: ").Append(FormatUtc(m.AddedUtc)).Append('\n');
                if (m.Title is { } title) sb.Append("    title: ").Append(Quote(title)).Append('\n');
                if (m.Cwd is { } cwd) sb.Append("    cwd: ").Append(Quote(cwd)).Append('\n');
            }
        }

        foreach (var extra in item.ExtraFrontMatter) sb.Append(extra.TrimEnd('\r')).Append('\n');
        sb.Append("---\n");

        var notes = (item.Notes ?? "").ReplaceLineEndings("\n").Trim('\n').TrimEnd();
        if (notes.Length > 0) sb.Append(notes).Append('\n');

        if (item.Log.Count > 0)
        {
            sb.Append('\n').Append(LogHeading).Append("\n\n");
            foreach (var e in item.Log) sb.Append(FormatLogLine(e)).Append('\n');
        }
        return sb.ToString();
    }

    internal static string FormatLogLine(WorkLogEntry e)
    {
        if (e.Raw is { } raw) return raw.TrimEnd();
        var text = e.Text.ReplaceLineEndings(" ").Trim();
        return "- " + e.At.ToString(LogTimeFormat, CultureInfo.InvariantCulture) + " " + (e.ByPerch ? PerchTag : "") + text;
    }

    // ── Scalars ──

    private static bool SplitKey(string line, out string key, out string value)
    {
        int colon = line.IndexOf(':');
        if (colon <= 0) { key = value = ""; return false; }
        key = line[..colon].Trim().ToLowerInvariant();
        value = line[(colon + 1)..].Trim();
        return key.Length > 0;
    }

    /// <summary>Reads a scalar: a JSON-style "double-quoted" string, a YAML 'single-quoted' one, or a bare value.</summary>
    internal static string Unquote(string value)
    {
        value = value.Trim();
        if (value == "[]" || value == "~" || value == "null") return "";
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            try { return JsonSerializer.Deserialize<string>(value) ?? ""; }
            catch { return value[1..^1]; }
        }
        if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
            return value[1..^1].Replace("''", "'");
        return value;
    }

    /// <summary>A double-quoted, JSON-escaped scalar, which reads back exactly whatever it holds.</summary>
    internal static string Quote(string? s)
    {
        var sb = new StringBuilder("\"");
        foreach (var c in s ?? "")
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }

    // Ids and hue names are plain tokens, written bare; anything else is quoted.
    private static string Scalar(string s) =>
        s.Length > 0 && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.') ? s : Quote(s);

    private static string FormatUtc(DateTime utc) =>
        DateTime.SpecifyKind(utc.Kind == DateTimeKind.Local ? utc.ToUniversalTime() : utc, DateTimeKind.Utc)
            .ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static DateTime? ParseUtc(string? s) =>
        DateTime.TryParse(s, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    private static WorkItemStatus ParseStatus(string s) => s.Trim().ToLowerInvariant() switch
    {
        "paused" => WorkItemStatus.Paused,
        "done" => WorkItemStatus.Done,
        _ => WorkItemStatus.Active,
    };

    private static WorkJoin ParseVia(string? s) => (s ?? "").Trim().ToLowerInvariant() switch
    {
        "launched" => WorkJoin.Launched,
        "terminal" => WorkJoin.Terminal,
        _ => WorkJoin.Manual,
    };

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}

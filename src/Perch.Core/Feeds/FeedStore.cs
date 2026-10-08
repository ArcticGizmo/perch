using System.Text.Json;
using Perch.Data;

namespace Perch.Feeds;

/// <summary>What Perch remembers about one subscription between runs: the last good document and the HTTP
/// validators, the last error, and the cached icon.</summary>
internal sealed class FeedCacheEntry
{
    /// <summary>The subscription URL this cache was fetched from — a changed URL invalidates it.</summary>
    public string SourceUrl { get; set; } = "";
    public FeedDoc? Doc { get; set; }
    public string? ETag { get; set; }
    public DateTimeOffset? LastModified { get; set; }
    public DateTime? FetchedUtc { get; set; }
    public string? FinalUrl { get; set; }
    public bool IsPrivate { get; set; }
    public string? Error { get; set; }
    public DateTime? ErrorUtc { get; set; }
    public string? IconFile { get; set; }
    public string? IconSourceUrl { get; set; }
    public DateTime? IconFetchedUtc { get; set; }
}

/// <summary>
/// The on-disk side of feeds, under <c>%AppData%/&lt;profile&gt;/feeds/</c> (docs/feeds-plan.md §2.1, §3.3):
/// <c>cache/&lt;id&gt;.json</c> per subscription, <c>read.json</c> for every read marker, <c>icons/&lt;id&gt;.&lt;ext&gt;</c>.
/// Writes are atomic (<see cref="AtomicFile"/>); reads are best-effort and never throw.
///
/// <para>Everything read back is held to the same rules as a fresh fetch: strings are re-cleaned with
/// <see cref="FeedText"/>, URLs re-vetted with <see cref="FeedUrl"/>, sizes re-capped. A hand-edited, stale or
/// corrupted cache file is therefore no way round the parser's defences. Subscription ids become file names, so
/// only <c>[A-Za-z0-9_-]{1,64}</c> is accepted — a hand-edited settings id can't walk out of the folder.</para>
/// </summary>
internal sealed class FeedStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    private static readonly string[] IconExtensions = [".png", ".jpg", ".gif", ".bmp", ".webp", ".ico"];

    private readonly string _root;

    public FeedStore(string root) => _root = root;

    public static FeedStore Default() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppProfile.DataFolderName, "feeds"));

    public string Root => _root;

    /// <summary>Whether <paramref name="id"/> is safe to use as a file name.</summary>
    public static bool IsValidId(string? id) =>
        id is { Length: > 0 and <= 64 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private string CachePath(string id) => Path.Combine(_root, "cache", id + ".json");
    private string ReadPath => Path.Combine(_root, "read.json");
    private string IconDir => Path.Combine(_root, "icons");

    // ── Cache ───────────────────────────────────────────────────────────────────────────────────────────────

    public FeedCacheEntry? LoadCache(string id)
    {
        if (!IsValidId(id)) return null;
        if (AtomicFile.TryRead(CachePath(id), out var json) != AtomicFile.ReadResult.Ok) return null;
        try
        {
            var e = JsonSerializer.Deserialize<FeedCacheEntry>(json, Json);
            return e is null ? null : Sanitize(id, e);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Serializes the entry (call under the caller's lock); returns the JSON to write, or null for an
    /// invalid id. Kept separate from <see cref="WriteCache"/> so the file write can happen outside a lock.</summary>
    public static string? SerializeCache(string id, FeedCacheEntry e) =>
        IsValidId(id) ? JsonSerializer.Serialize(e, Json) : null;

    public void WriteCache(string id, string json)
    {
        if (!IsValidId(id)) return;
        try { AtomicFile.Write(CachePath(id), json); } catch { }
    }

    public void SaveCache(string id, FeedCacheEntry e)
    {
        if (SerializeCache(id, e) is { } json) WriteCache(id, json);
    }

    /// <summary>Forgets a subscription's cache and icon (it was removed, or re-pointed at another URL).</summary>
    public void DeleteFeed(string id)
    {
        if (!IsValidId(id)) return;
        TryDelete(CachePath(id));
        foreach (var ext in IconExtensions) TryDelete(Path.Combine(IconDir, id + ext));
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    // ── Read state ──────────────────────────────────────────────────────────────────────────────────────────

    public Dictionary<string, FeedReadRecord> LoadRead()
    {
        var result = new Dictionary<string, FeedReadRecord>(StringComparer.Ordinal);
        if (AtomicFile.TryRead(ReadPath, out var json) != AtomicFile.ReadResult.Ok) return result;
        try
        {
            var raw = JsonSerializer.Deserialize<Dictionary<string, FeedReadRecord>>(json, Json);
            if (raw is null) return result;
            foreach (var (id, rec) in raw)
            {
                if (!IsValidId(id) || rec is null) continue;
                var clean = new FeedReadRecord { PrimedUtc = rec.PrimedUtc };
                foreach (var (entryId, at) in (rec.Read ?? []).Take(FeedReadState.MaxMarkers))
                    if (entryId is { Length: > 0 and <= 2048 })
                        clean.Read[entryId] = DateTime.SpecifyKind(at, DateTimeKind.Utc);
                result[id] = clean;
            }
        }
        catch { }
        return result;
    }

    public static string SerializeRead(Dictionary<string, FeedReadRecord> records) => JsonSerializer.Serialize(records, Json);

    public void WriteRead(string json)
    {
        try { AtomicFile.Write(ReadPath, json); } catch { }
    }

    // ── Icons ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Stores icon bytes that pass <see cref="FeedIcon.Validate"/>; returns the file name, or null.</summary>
    public string? SaveIcon(string id, byte[] bytes)
    {
        if (!IsValidId(id) || FeedIcon.Validate(bytes) is not { } ext) return null;
        try
        {
            foreach (var old in IconExtensions) if (old != ext) TryDelete(Path.Combine(IconDir, id + old));
            AtomicFile.Write(Path.Combine(IconDir, id + ext), fs => fs.Write(bytes, 0, bytes.Length));
            return id + ext;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The full path of a cached icon, when the name is one this store would have written and it exists.</summary>
    public string? IconPath(string id, string? iconFile)
    {
        if (!IsValidId(id) || iconFile is null) return null;
        if (!IconExtensions.Any(ext => iconFile == id + ext)) return null;
        var path = Path.Combine(IconDir, iconFile);
        return File.Exists(path) ? path : null;
    }

    // ── Re-validation ───────────────────────────────────────────────────────────────────────────────────────

    private static FeedCacheEntry Sanitize(string id, FeedCacheEntry e)
    {
        e.SourceUrl ??= "";
        e.Doc = e.Doc is null ? null : Reclean(e.Doc);
        e.ETag = e.ETag is { Length: <= 256 } tag && !tag.Any(char.IsControl) ? tag : null;
        e.FinalUrl = FeedUrl.SafeString(e.FinalUrl, null);
        e.Error = e.Error is null ? null : FeedText.Clean(e.Error, 200);
        e.IconFile = e.IconFile is not null && IconExtensions.Any(ext => e.IconFile == id + ext) ? e.IconFile : null;
        e.IconSourceUrl = FeedUrl.SafeString(e.IconSourceUrl, null);
        return e;
    }

    /// <summary>Holds a document from anywhere but the parser (the cache) to the parser's output rules.</summary>
    internal static FeedDoc Reclean(FeedDoc d)
    {
        var entries = (d.Entries ?? []).Where(x => x is not null).Take(FeedParser.MaxEntries).Select(x => new FeedEntry(
            Id: x.Id is { Length: > 0 and <= 2048 } ? x.Id : Guid.NewGuid().ToString("N"),
            Title: FeedText.Clean(x.Title, FeedText.TitleMax) is { Length: > 0 } t ? t : "(untitled)",
            Url: FeedUrl.SafeString(x.Url, null),
            Author: FeedText.Clean(x.Author, FeedText.AuthorMax) is { Length: > 0 } a ? a : null,
            Published: Utc(x.Published),
            Updated: Utc(x.Updated) ?? DateTime.UnixEpoch,
            ContentHtml: x.ContentHtml is { Length: > HtmlToMarkdown.MaxInput * 2 } big ? big[..(HtmlToMarkdown.MaxInput * 2)] : x.ContentHtml,
            ContentBase: FeedUrl.SafeString(x.ContentBase, null),
            SummaryText: FeedText.Clean(x.SummaryText, FeedText.SummaryMax) is { Length: > 0 } s ? s : null)).ToList();

        return new FeedDoc(
            FeedText.Clean(d.Title, FeedText.FeedTitleMax),
            FeedUrl.SafeString(d.SiteUrl, null),
            FeedUrl.SafeString(d.IconUrl, null),
            Utc(d.Updated),
            entries);
    }

    private static DateTime? Utc(DateTime? d) => d is { } v ? DateTime.SpecifyKind(v, DateTimeKind.Utc) : null;
}

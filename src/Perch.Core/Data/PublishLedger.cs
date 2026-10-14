using System.Text.Json;
using System.Text.Json.Nodes;

namespace Perch.Data;

/// <summary>A background publish in flight: the <c>claude --bg</c> worker doing it, the session it's for, and the file.</summary>
public sealed record PendingPublish(string ShortId, string ParentSessionId, string File, DateTime StartedAt);

/// <summary>
/// Perch's record of the artifacts it publishes on a session's behalf (<see cref="ArtifactPublisher"/>). The publish
/// runs in a separate background session, so its result lands in <em>that</em> transcript, not the parent's: the
/// parent's artifact glyph and the "daemon" strip can't tell on their own. Two small stores in Perch's data folder,
/// written by whichever process publishes (the tray, or <c>perch artifact publish</c> inside a session) and read by the
/// tray's scans:
/// <list type="bullet">
///   <item><c>pending/{shortId}.json</c> — one per publish in flight. The parent session shows it as a child row, and
///   the daemon strip leaves that worker out (it's Perch's plumbing, not a session the user started).</item>
///   <item><c>published/{parentSessionId}.jsonl</c> — one line per published artifact, merged into the parent's
///   <see cref="ClaudeSession.Artifacts"/>.</item>
/// </list>
/// Best-effort throughout: IO failures are swallowed and a read never throws.
/// </summary>
public static class PublishLedger
{
    // A pending marker older than this belongs to a publish that died without cleaning up; it's ignored.
    private static readonly TimeSpan PendingMaxAge = ArtifactPublisher.Timeout + TimeSpan.FromMinutes(1);

    /// <summary>Test seam: the ledger's root folder (default: Perch's data folder).</summary>
    internal static string? RootOverride { get; set; }

    private static string Root => RootOverride ?? Path.Combine(PerchSessionPlugin.DataDir, "artifact-publishing");
    private static string PendingDir => Path.Combine(Root, "pending");
    private static string PublishedDir => Path.Combine(Root, "published");

    public static void BeginPublish(string shortId, string parentSessionId, string file)
    {
        if (!IsToken(shortId) || !ClaudeCli.IsSessionId(parentSessionId)) return;
        var json = new JsonObject
        {
            ["shortId"] = shortId, ["parentSessionId"] = parentSessionId, ["file"] = file,
            ["startedAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        TryIo(() =>
        {
            Directory.CreateDirectory(PendingDir);
            File.WriteAllText(Path.Combine(PendingDir, shortId + ".json"), json.ToJsonString());
        });
    }

    public static void EndPublish(string shortId)
    {
        if (IsToken(shortId)) TryIo(() => File.Delete(Path.Combine(PendingDir, shortId + ".json")));
    }

    /// <summary>The publishes in flight (stale markers skipped).</summary>
    public static IReadOnlyList<PendingPublish> Pending()
    {
        var list = new List<PendingPublish>();
        try
        {
            if (!Directory.Exists(PendingDir)) return list;
            foreach (var path in Directory.EnumerateFiles(PendingDir, "*.json"))
            {
                try
                {
                    if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject o) continue;
                    var shortId = TranscriptJson.AsString(o["shortId"]);
                    var parent = TranscriptJson.AsString(o["parentSessionId"]);
                    var file = TranscriptJson.AsString(o["file"]) ?? "";
                    var started = DateTimeOffset.FromUnixTimeMilliseconds(TranscriptJson.AsLong(o["startedAt"])).UtcDateTime;
                    if (shortId is null || parent is null || DateTime.UtcNow - started > PendingMaxAge) continue;
                    list.Add(new PendingPublish(shortId, parent, file, started));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return list;
    }

    /// <summary>The child rows a session shows for its publishes in flight: display-only <see cref="SubAgent"/>s, never
    /// fed to the status gates (a publish isn't the session's own work, so it mustn't hold or fire its done alert).</summary>
    public static IReadOnlyList<SubAgent> PendingRowsFor(IReadOnlyList<PendingPublish> pending, string sessionId)
    {
        List<SubAgent>? rows = null;
        foreach (var p in pending)
        {
            if (p.ParentSessionId != sessionId) continue;
            var leaf = PathLeaf.Of(p.File);
            (rows ??= []).Add(new SubAgent("perch-publish-" + p.ShortId, $"Publishing {leaf} as an artifact", "artifact",
                Activity: $"Publishing {leaf} as an artifact"));
        }
        return rows ?? (IReadOnlyList<SubAgent>)[];
    }

    public static void RecordPublished(string parentSessionId, PublishedArtifact artifact)
    {
        if (!ClaudeCli.IsSessionId(parentSessionId) || !TranscriptReader.IsArtifactUrl(artifact.Url)) return;
        var line = new JsonObject { ["url"] = artifact.Url, ["title"] = artifact.Title }.ToJsonString();
        TryIo(() =>
        {
            Directory.CreateDirectory(PublishedDir);
            File.AppendAllText(Path.Combine(PublishedDir, parentSessionId + ".jsonl"), line + "\n");
        });
    }

    /// <summary>The artifacts Perch published for <paramref name="sessionId"/>, oldest first, de-duplicated by URL (last
    /// title wins). Cached by the file's write time, so an unchanged ledger costs a stat per scan.</summary>
    public static IReadOnlyList<Artifact> PublishedFor(string sessionId)
    {
        if (!ClaudeCli.IsSessionId(sessionId)) return [];
        var path = Path.Combine(PublishedDir, sessionId + ".jsonl");
        try
        {
            var stamp = File.GetLastWriteTimeUtc(path);   // 1601-01-01 when missing
            if (Cache.TryGetValue(path, out var hit) && hit.Stamp == stamp) return hit.Artifacts;
            var artifacts = File.Exists(path) ? Parse(File.ReadAllLines(path)) : [];
            Cache[path] = (stamp, artifacts);
            return artifacts;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime Stamp, IReadOnlyList<Artifact> Artifacts)> Cache = new();

    internal static IReadOnlyList<Artifact> Parse(IEnumerable<string> lines)
    {
        var order = new List<string>();
        var titles = new Dictionary<string, string>();
        foreach (var line in lines)
        {
            try
            {
                if (JsonNode.Parse(line) is not JsonObject o) continue;
                var url = TranscriptJson.AsString(o["url"]);
                if (!TranscriptReader.IsArtifactUrl(url)) continue;
                var title = TranscriptJson.AsString(o["title"]);
                if (!titles.ContainsKey(url)) order.Add(url);
                titles[url] = string.IsNullOrWhiteSpace(title) ? "Untitled artifact" : title.Trim();
            }
            catch (JsonException) { }
        }
        return order.Select(u => new Artifact(u, titles[u])).ToList();
    }

    /// <summary><paramref name="fromTranscript"/> plus any of <paramref name="fromLedger"/> it doesn't already carry.</summary>
    public static IReadOnlyList<Artifact> Merge(IReadOnlyList<Artifact> fromTranscript, IReadOnlyList<Artifact> fromLedger)
    {
        if (fromLedger.Count == 0) return fromTranscript;
        var seen = fromTranscript.Select(a => a.Url).ToHashSet(StringComparer.Ordinal);
        var extra = fromLedger.Where(a => !seen.Contains(a.Url)).ToList();
        return extra.Count == 0 ? fromTranscript : [.. fromTranscript, .. extra];
    }

    private static bool IsToken(string s) => s.Length is > 0 and <= 64 && s.All(char.IsAsciiLetterOrDigit);

    private static void TryIo(Action io)
    {
        try { io(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

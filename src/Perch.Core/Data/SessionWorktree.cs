using System.Text.Json.Nodes;

namespace Perch.Data;

/// <summary>The worktree a session's branch-scoped integrations (the PR glyph, the Jira ticket) read from instead of
/// its cwd: <see cref="Dir"/> is that worktree's root, and <see cref="Pinned"/> says whether the user chose it from
/// the row menu rather than it being inferred from the session's edits. See <see cref="SessionWorktree"/>.</summary>
public readonly record struct SessionWorktreeChoice(string Dir, bool Pinned);

/// <summary>
/// Which working tree a session is really working in. Claude Code's recorded cwd never leaves the folder the session
/// started in: a session that runs <c>git worktree add ..\repo--feature</c> and then edits there by absolute path
/// (and commits with <c>git -C</c>) still reports the main checkout, so its PR glyph showed the main checkout's PR.
/// In order, the tree is: a pin the user set from the row menu (<see cref="WritePin"/>); else the worktree holding
/// the session's most recent successful file write; else none (the cwd stands).
///
/// <para>Only worktrees git itself records for the cwd's repo are candidates, and transcript paths are matched
/// against them as strings. A path the model wrote is never probed on disk: a UNC path there would open an SMB
/// connection. Writes outside the repo (memory files, another repo) are skipped, so they never steal the glyph.</para>
///
/// <para>Not thread-safe: owned by the monitor and driven by one scan at a time, like the transcript readers.</para>
/// </summary>
internal sealed class SessionWorktree
{
    // Enough recent writes to see past a burst of memory/scratch edits outside the repo to the last one inside it.
    private const int KeptWrites = 32;

    // The worktree list changes only on `git worktree add/remove`, so a scan re-reads it at most this often per repo.
    private static readonly TimeSpan SetTtl = TimeSpan.FromSeconds(30);

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static readonly LineFolder<WritesState> WritesFolder = new(() => new(), StepWrites);
    private readonly TranscriptFold _fold = new(WritesFolder);
    private readonly Dictionary<string, (GitWorktreeSet? Set, DateTime At)> _sets =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    /// <summary>The worktree <paramref name="sessionId"/> is working in when it isn't the one holding
    /// <paramref name="cwd"/>, or a pinned choice; null when the cwd stands. Best-effort; never throws.</summary>
    public SessionWorktreeChoice? Resolve(string sessionId, string cwd)
    {
        try
        {
            if (string.IsNullOrEmpty(sessionId) || GitCheckoutScanner.FindRoot(cwd) is not { } root)
                return null;
            var transcript = TranscriptLocator.Resolve(sessionId, cwd);
            var pin = ReadPin(PinPath(sessionId, cwd, transcript));
            var writes = transcript is null ? [] : WritesAt(transcript);
            if (pin is null && writes.Count == 0)
                return null;
            return WorktreesOf(root) is { } set ? Pick(root, pin, writes, set) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The rule behind <see cref="Resolve"/>, over already-read inputs: <paramref name="pin"/> wins when it is still
    /// one of the repo's worktrees (a removed worktree's pin is ignored); otherwise the newest of
    /// <paramref name="writesNewestFirst"/> that lands in any worktree decides, and landing in
    /// <paramref name="root"/>'s own means no change. The deepest matching worktree owns a path, so a nested
    /// <c>.claude/worktrees/x</c> beats the checkout around it. Internal for unit testing.
    /// </summary>
    internal static SessionWorktreeChoice? Pick(string root, string? pin, IReadOnlyList<string> writesNewestFirst, GitWorktreeSet set)
    {
        var trees = Trees(set).Select(t => t.Path).ToList();
        if (trees.Count < 2)
            return null;

        if (pin is not null && trees.FirstOrDefault(t => SamePath(t, pin)) is { } pinned)
            return new SessionWorktreeChoice(pinned, Pinned: true);

        foreach (var path in writesNewestFirst)
        {
            string? owner = null;
            foreach (var t in trees)
                if (IsUnder(path, t) && (owner is null || t.Length > owner.Length))
                    owner = t;
            if (owner is not null)
                return SamePath(owner, root) ? null : new SessionWorktreeChoice(owner, Pinned: false);
        }
        return null;
    }

    /// <summary>Every working tree of the repo holding <paramref name="cwd"/> (the main checkout first, unless the
    /// repo is bare), or null when it isn't a repo or has no linked worktrees, so there is nothing to choose
    /// between. Reads a few small files under the repo's git dir; never throws.</summary>
    public static IReadOnlyList<GitWorktree>? WorktreesFor(string cwd)
    {
        try
        {
            if (GitCheckoutScanner.FindRoot(cwd) is not { } root || GitWorktreeScanner.Read(root) is not { } set)
                return null;
            var trees = Trees(set);
            return trees.Count < 2 ? null : trees;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Pins <paramref name="sessionId"/>'s PR/Jira lookups to <paramref name="worktree"/>, or clears the
    /// pin (back to inferring it) when null. Stored beside the session's transcript, so it survives the session
    /// ending and returns on a resume; the <c>sessions/</c> sidecars are swept at SessionEnd. Best-effort.</summary>
    public static void WritePin(string sessionId, string cwd, string? worktree)
    {
        if (PinPath(sessionId, cwd, TranscriptLocator.Resolve(sessionId, cwd)) is not { } path)
            return;
        try
        {
            if (string.IsNullOrEmpty(worktree))
            {
                if (File.Exists(path))
                    File.Delete(path);
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, worktree);
        }
        catch
        {
            // Best-effort: the next scan reflects whatever is on disk.
        }
    }

    // Beside the transcript when it exists; before the first transcript record, in the project folder it will land in.
    internal static string? PinPath(string sessionId, string cwd, string? transcript)
    {
        if (string.IsNullOrEmpty(sessionId))
            return null;
        var dir = transcript is not null ? Path.GetDirectoryName(transcript)
            : string.IsNullOrEmpty(cwd) ? null
            : Path.Combine(ClaudePaths.ProjectsDir, TranscriptLocator.EncodeProjectDir(cwd));
        return dir is null ? null : Path.Combine(dir, sessionId + ".perch-worktree");
    }

    private static string? ReadPin(string? path)
    {
        try
        {
            return path is not null && File.Exists(path) && File.ReadAllText(path).Trim() is { Length: > 0 } pin ? pin : null;
        }
        catch
        {
            return null;
        }
    }

    // The path-based form of the transcript read, newest first: the seam tests drive.
    internal IReadOnlyList<string> WritesAt(string transcript) =>
        _fold.Get(transcript, WritesFolder, FinishWrites, (IReadOnlyList<string>)[]);

    private GitWorktreeSet? WorktreesOf(string root)
    {
        var now = DateTime.UtcNow;
        if (_sets.TryGetValue(root, out var cached) && now - cached.At < SetTtl)
            return cached.Set;
        var set = GitWorktreeScanner.Read(root);
        _sets[root] = (set, now);
        return set;
    }

    private static List<GitWorktree> Trees(GitWorktreeSet set) =>
        set.Bare ? [.. set.Linked] : [new GitWorktree(set.Root, set.MainBranch), .. set.Linked];

    private sealed class WritesState
    {
        public readonly List<(string Id, string Path)> Recent = new();
    }

    // A write's paired tool_result follows it, so a failed one is dropped as soon as its is_error arrives.
    private static void StepWrites(WritesState s, string line)
    {
        bool maybeWrite = line.Contains("tool_use") && (line.Contains("file_path") || line.Contains("notebook_path"));
        if (!maybeWrite && !line.Contains("is_error"))
            return;

        try
        {
            if (TranscriptJson.ContentArray(JsonNode.Parse(line)) is not { } content)
                return;

            foreach (var block in content)
            {
                var type = TranscriptJson.BlockType(block);
                if (type == "tool_use")
                {
                    var id = block!["id"]?.GetValue<string>();
                    if (string.IsNullOrEmpty(id) || WritePathKey(block["name"]?.GetValue<string>()) is not { } key)
                        continue;
                    if (block["input"]?[key]?.GetValue<string>() is not { Length: > 0 } file || !Path.IsPathFullyQualified(file))
                        continue;
                    s.Recent.Add((id, file));
                    if (s.Recent.Count > KeptWrites)
                        s.Recent.RemoveAt(0);
                }
                else if (type == "tool_result"
                         && block!["is_error"]?.GetValue<bool>() == true
                         && block["tool_use_id"]?.GetValue<string>() is { } rid)
                {
                    s.Recent.RemoveAll(w => w.Id == rid);
                }
            }
        }
        catch
        {
            // Malformed/partial line (transcripts are appended live) — skip it.
        }
    }

    private static IReadOnlyList<string> FinishWrites(WritesState s)
    {
        var newestFirst = new List<string>(s.Recent.Count);
        for (int i = s.Recent.Count - 1; i >= 0; i--)
            newestFirst.Add(s.Recent[i].Path);
        return newestFirst;
    }

    private static string? WritePathKey(string? tool) => tool switch
    {
        "Write" or "Edit" or "MultiEdit" => "file_path",
        "NotebookEdit"                    => "notebook_path",
        _                                 => null,
    };

    // Transcripts carry paths from whatever OS wrote them, so separators are unified before comparing.
    private static string Norm(string path) => path.Replace('\\', '/').TrimEnd('/');

    private static bool IsUnder(string path, string dir) =>
        Norm(path).StartsWith(Norm(dir) + "/", PathComparison);

    private static bool SamePath(string a, string b) => string.Equals(Norm(a), Norm(b), PathComparison);
}

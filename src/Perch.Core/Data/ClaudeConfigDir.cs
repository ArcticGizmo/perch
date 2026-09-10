namespace Perch.Data;

using System.Collections.Concurrent;
using System.Text.Json.Nodes;

/// <summary>How a config dir's signed-in organization compares with the one its manifest declares.
/// Only <see cref="Mismatch"/> is a misconfiguration.</summary>
public enum OrgState
{
    /// <summary>No readable <c>.claude.json</c>.</summary>
    Unknown = 0,
    /// <summary>The file exists but names no organization: never signed in.</summary>
    NotSignedIn = 1,
    Match = 2,
    Mismatch = 3,
}

public sealed record ClaudeAccount(string? Org, string? Email)
{
    public static ClaudeAccount None { get; } = new(null, null);
}

/// <summary>
/// One Claude Code config directory and the paths Perch reads or writes beneath it.
///
/// <para>There can be several: a per-account environment scheme fronts Claude Code with a config dir
/// per organization, and a session's live state lands under whichever one launched it. So this is a
/// value, discovered as a set (<see cref="ClaudeConfigSet"/>) and carried on each
/// <see cref="ClaudeSession"/>. Nothing here touches the disk except <see cref="Account"/>.</para>
/// </summary>
public sealed record ClaudeConfigDir
{
    /// <param name="realRoot">Links resolved, for de-duplicating the set.</param>
    public ClaudeConfigDir(
        string root,
        string? realRoot = null,
        string? slug = null,
        string? label = null,
        bool isHub = false,
        string? declaredOrg = null,
        string? declaredAccount = null)
    {
        Root = root;
        RealRoot = realRoot ?? root;
        Slug = slug;
        IsHub = isHub;
        Label = !string.IsNullOrWhiteSpace(label) ? label!
              : !string.IsNullOrWhiteSpace(slug) ? slug!
              : isHub ? "Hub"
              : Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        DeclaredOrg = declaredOrg;
        DeclaredAccount = declaredAccount;
    }

    public string Root { get; }
    public string RealRoot { get; }
    public string? Slug { get; }
    public string Label { get; }
    public bool IsHub { get; }

    /// <summary>The organization the manifest says this env must be signed in to, if any.</summary>
    public string? DeclaredOrg { get; }
    public string? DeclaredAccount { get; }

    /// <summary>Live session sidecars: <c>{pid}.json</c> plus the <c>.mode</c> / <c>.notify</c> /
    /// <c>.history</c> / <c>.perch-lock</c> markers beside them.</summary>
    public string SessionsDir => Path.Combine(Root, "sessions");

    public string ProjectsDir => Path.Combine(Root, "projects");
    public string PluginsDir => Path.Combine(Root, "plugins");
    public string DaemonDir => Path.Combine(Root, "daemon");
    public string DaemonRosterFile => Path.Combine(DaemonDir, "roster.json");
    public string ImageCacheDir => Path.Combine(Root, "image-cache");

    /// <summary>Per-account and never shared between config dirs — never merge or cross-read.</summary>
    public string CredentialsFile => Path.Combine(Root, ".credentials.json");

    /// <summary>Per config dir by design (model, statusLine and plugin enablement all differ).</summary>
    public string UserSettingsFile => Path.Combine(Root, "settings.json");

    /// <summary>Claude Code keeps this inside the config dir when <c>CLAUDE_CONFIG_DIR</c> is set, but
    /// at <c>~/.claude.json</c> — one level <em>above</em> <c>~/.claude</c> — for the default dir.</summary>
    public string ClaudeJsonFile => IsHub
        ? Path.Combine(Directory.GetParent(Root.TrimEnd(Path.DirectorySeparatorChar,
              Path.AltDirectorySeparatorChar))?.FullName ?? Root, ".claude.json")
        : Path.Combine(Root, ".claude.json");

    public bool HasCredentials => File.Exists(CredentialsFile);

    /// <summary>Cached against the file's write time: Claude Code rewrites it constantly and it runs
    /// to tens of kilobytes.</summary>
    public ClaudeAccount Account => ReadAccount(ClaudeJsonFile);

    /// <summary>The organization actually signed in to, else the declared one.</summary>
    public string? Org
    {
        get
        {
            var signedIn = Account.Org;
            return !string.IsNullOrWhiteSpace(signedIn) ? signedIn
                 : !string.IsNullOrWhiteSpace(DeclaredOrg) ? DeclaredOrg
                 : null;
        }
    }

    public OrgState OrgState
    {
        get
        {
            if (!File.Exists(ClaudeJsonFile)) return OrgState.Unknown;
            if (string.IsNullOrWhiteSpace(Account.Org)) return OrgState.NotSignedIn;
            if (string.IsNullOrWhiteSpace(DeclaredOrg)) return OrgState.Match;
            return string.Equals(Account.Org, DeclaredOrg, StringComparison.OrdinalIgnoreCase)
                ? OrgState.Match
                : OrgState.Mismatch;
        }
    }

    /// <summary>
    /// Which config dirs share a usage reading. Dirs on the same account <em>and</em> organization are
    /// polled once between them; a different org is polled separately, because whether those limits
    /// are account-wide or org-scoped can't be told from the response. Falls back to the path so an
    /// unidentifiable dir is never merged with another.
    /// </summary>
    public string UsageKey
    {
        get
        {
            var email = !string.IsNullOrWhiteSpace(Account.Email) ? Account.Email : DeclaredAccount;
            var org = Org;
            return string.IsNullOrWhiteSpace(email) && string.IsNullOrWhiteSpace(org)
                ? "root:" + RealRoot.ToLowerInvariant()
                : $"{email}|{org}";
        }
    }

    /// <summary>Label plus organization. The org alone is not unique — a hub and an env can report the
    /// same one.</summary>
    public string DisplayName =>
        Org is { Length: > 0 } org && !string.Equals(org, Label, StringComparison.OrdinalIgnoreCase)
            ? $"{Label} · {org}"
            : Label;

    public override string ToString() => Root;

    // By resolved path only: two records for one physical directory must never both be in the set.
    public bool Equals(ClaudeConfigDir? other) =>
        other is not null && PathComparer.Equals(RealRoot, other.RealRoot);

    public override int GetHashCode() => PathComparer.GetHashCode(RealRoot);

    internal static StringComparer PathComparer =>
        OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    private static readonly ConcurrentDictionary<string, (DateTime Stamp, ClaudeAccount Account)> AccountCache =
        new(PathComparer);

    private static ClaudeAccount ReadAccount(string path)
    {
        DateTime stamp;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return ClaudeAccount.None;
            stamp = info.LastWriteTimeUtc;
        }
        catch { return ClaudeAccount.None; }

        if (AccountCache.TryGetValue(path, out var cached) && cached.Stamp == stamp)
            return cached.Account;

        var account = ParseAccount(path);
        AccountCache[path] = (stamp, account);
        return account;
    }

    private static ClaudeAccount ParseAccount(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            if (JsonNode.Parse(reader.ReadToEnd()) is not JsonObject root)
                return ClaudeAccount.None;
            var oauth = root["oauthAccount"] as JsonObject;
            return new ClaudeAccount(
                Blank(TranscriptJson.AsString(oauth?["organizationName"])),
                Blank(TranscriptJson.AsString(oauth?["emailAddress"])));
        }
        catch { return ClaudeAccount.None; }

        static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
    }
}

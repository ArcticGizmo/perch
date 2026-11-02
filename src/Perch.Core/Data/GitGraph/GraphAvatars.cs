using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Perch.Data.GitGraph;

/// <summary>
/// Author pictures for the graph's commit nodes, the pure half: initials and a colour for an author with no picture,
/// the avatar a GitHub "noreply" commit email names outright, and the batched GraphQL lookup (one commit per author)
/// that finds the GitHub account behind any other email. <see cref="AvatarFetcher"/> runs it. Gravatar is deliberately
/// not used: it would send a hash of every author's email to a third party.
/// </summary>
public static class GraphAvatars
{
    /// <summary>The only host avatar images are downloaded from.</summary>
    public const string AvatarHost = "avatars.githubusercontent.com";

    /// <summary>The pixel size asked for: the node is ~19 DIP, so this stays crisp at 200%+ scaling.</summary>
    public const int Size = 64;

    /// <summary>The most authors one lookup asks about; the next load asks about the rest.</summary>
    public const int BatchSize = 100;

    // GitHub's noreply form: "<id>+<login>@users.noreply.github.com" (bots too: "49699333+dependabot[bot]@…").
    private static readonly Regex NoReply = new(@"^(?<id>\d+)\+[^@\s]+@users\.noreply\.github\.com$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>Up to two letters for an author with no picture: first and last word of the name ("Jon Howell" → "JH",
    /// "dependabot[bot]" → "D"), else the email's first letter, else "?".</summary>
    public static string Initials(string name, string email)
    {
        var words = Regex.Replace(name, @"\[bot\]$|[^\p{L}\p{N}\s]", " ", RegexOptions.CultureInvariant)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
            return email.Length > 0 && char.IsLetterOrDigit(email[0]) ? char.ToUpperInvariant(email[0]).ToString() : "?";
        var first = char.ToUpperInvariant(words[0][0]).ToString();
        return words.Length == 1 ? first : first + char.ToUpperInvariant(words[^1][0]);
    }

    /// <summary>A stable index into a colour set of <paramref name="count"/> for an author (FNV-1a of the lowercased
    /// email), so the same person is the same colour on every row and every run.</summary>
    public static int ColorIndex(string email, int count)
    {
        uint h = 2166136261;
        foreach (var ch in email.ToLowerInvariant())
            h = (h ^ ch) * 16777619;
        return count <= 0 ? 0 : (int)(h % (uint)count);
    }

    /// <summary>The avatar of a GitHub noreply email, which carries the account id, so no lookup is needed. Null for
    /// any other email.</summary>
    public static string? NoReplyUrl(string email) =>
        NoReply.Match(email.Trim()) is { Success: true } m ? $"https://{AvatarHost}/u/{m.Groups["id"].Value}?s={Size}&v=4" : null;

    /// <summary>True for an https URL on <see cref="AvatarHost"/>: a lookup's answer is only ever fetched from there.</summary>
    public static bool IsAvatarUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps
        && string.Equals(u.Host, AvatarHost, StringComparison.OrdinalIgnoreCase);

    /// <summary>A local cache name for an author: the email itself is never written to disk.</summary>
    public static string CacheKey(string email) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(email.Trim().ToLowerInvariant())))[..32].ToLowerInvariant();

    /// <summary>
    /// The GitHub repository to ask about, from a checkout's remotes: <c>origin</c> first, then any other. github.com,
    /// or an SSH alias with no dot in it (<c>git@github-work:acme/web</c>), which is how people point a second key at
    /// github.com. Any other host (GitHub Enterprise, GitLab) gives null: its avatars live elsewhere.
    /// </summary>
    public static GitRepoRef? GitHubRepo(IReadOnlyList<GitRemoteEntry> remotes)
    {
        foreach (var r in remotes.OrderBy(r => r.Name == "origin" ? 0 : 1))
            if (GitRemote.Parse(r.Url) is { } repo && (repo.Host == "github.com" || !repo.Host.Contains('.')))
                return repo;
        return null;
    }

    /// <summary>
    /// One commit per author not in <paramref name="known"/>, at most <paramref name="max"/> authors in the order they
    /// first appear (newest first): what the next lookup asks about. Each author's <em>oldest</em> loaded commit is the
    /// one asked about, since GitHub only answers for commits that have been pushed and the newest are often not yet.
    /// WIP and stash rows have no author and are skipped.
    /// </summary>
    public static List<(string Email, string Oid)> PickCommits(IEnumerable<GraphCommit> commits, IReadOnlySet<string> known, int max = BatchSize)
    {
        var order = new List<string>();
        var oldest = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in commits)
        {
            if (c.IsPseudo || c.AuthorEmail.Length == 0 || known.Contains(c.AuthorEmail)) continue;
            if (!oldest.ContainsKey(c.AuthorEmail))
            {
                if (order.Count >= max) continue;
                order.Add(c.AuthorEmail);
            }
            oldest[c.AuthorEmail] = c.Hash;
        }
        return order.Select(e => (e, oldest[e])).ToList();
    }

    /// <summary>The <c>gh api graphql --input -</c> body asking for each commit's author's avatar, one alias per commit.</summary>
    public static string BuildQuery(GitRepoRef repo, IReadOnlyList<(string Email, string Oid)> picks)
    {
        var q = new StringBuilder("query { repository(owner: ").Append(JsonSerializer.Serialize(repo.Owner))
            .Append(", name: ").Append(JsonSerializer.Serialize(repo.Repo)).Append(") {");
        for (int i = 0; i < picks.Count; i++)
            if (IsHash(picks[i].Oid))
                q.Append($" a{i}: object(oid: \"{picks[i].Oid}\") {{ ... on Commit {{ author {{ user {{ avatarUrl(size: {Size}) }} }} }} }}");
        q.Append(" } }");
        return JsonSerializer.Serialize(new Dictionary<string, string> { ["query"] = q.ToString() });
    }

    /// <summary>
    /// Each asked-about email's avatar URL from a lookup's response, or null when GitHub has the commit but no account
    /// for its author. A commit GitHub doesn't have (not pushed) leaves its email out, as does a response that isn't JSON
    /// or has no data (gh failed, no access): not knowing is never recorded as "no picture". Only
    /// <see cref="AvatarHost"/> URLs are kept.
    /// </summary>
    public static Dictionary<string, string?> ParseResponse(string json, IReadOnlyList<(string Email, string Oid)> picks)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
                || !data.TryGetProperty("repository", out var repo) || repo.ValueKind != JsonValueKind.Object)
                return result;
            for (int i = 0; i < picks.Count; i++)
            {
                if (!repo.TryGetProperty($"a{i}", out var obj) || obj.ValueKind != JsonValueKind.Object) continue;
                string? url = null;
                if (obj.TryGetProperty("author", out var author) && author.ValueKind == JsonValueKind.Object
                    && author.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.Object
                    && user.TryGetProperty("avatarUrl", out var a) && a.ValueKind == JsonValueKind.String)
                    url = a.GetString();
                result[picks[i].Email] = IsAvatarUrl(url) ? url : null;
            }
        }
        catch (JsonException)
        {
        }
        return result;
    }

    private static bool IsHash(string s) => s.Length is >= 7 and <= 64 && s.All(Uri.IsHexDigit);
}

using System.Text.RegularExpressions;

namespace Perch.Data.GitGraph;

/// <summary>Someone named in a commit: its author, committer or a co-author.</summary>
public sealed record GitPerson(string Name, string Email);

/// <summary>
/// What the graph's commit details show above a commit's files: who wrote it and when, who committed it when that
/// was someone else or later, the <c>Co-authored-by</c> trailers, and the whole message (the graph itself only loads
/// the subject).
/// </summary>
public sealed record CommitInfo(string Hash, GitPerson Author, DateTimeOffset Authored, GitPerson Committer,
    DateTimeOffset Committed, IReadOnlyList<string> Parents, string Message, IReadOnlyList<GitPerson> CoAuthors)
{
    /// <summary>A rebase, cherry-pick or amend re-commits someone's work: worth saying when it was another person,
    /// or the same person well after they wrote it.</summary>
    public bool CommittedSeparately =>
        !string.Equals(Committer.Email, Author.Email, StringComparison.OrdinalIgnoreCase)
        || (Committed - Authored).Duration() > TimeSpan.FromMinutes(1);
}

/// <summary>Reads one commit's <see cref="CommitInfo"/>: one <c>git show -s</c>, blocking, so call it off the UI thread.</summary>
internal static partial class CommitInfoReader
{
    private const string Format = "--format=%H%x1f%an%x1f%ae%x1f%at%x1f%cn%x1f%ce%x1f%ct%x1f%P%x1f%B";

    public static CommitInfo? Load(string repo, string hash)
    {
        if (hash.Length == 0 || hash[0] == '-') return null;
        var r = GitRunner.Run(repo, 10_000, "show", "-s", "--no-show-signature", Format, hash);
        return r.Exit == 0 ? Parse(r.Stdout) : null;
    }

    /// <summary>Pure half of <see cref="Load"/>: the fields are unit-separated, and the message (last) keeps any
    /// separator it happens to contain.</summary>
    public static CommitInfo? Parse(string output)
    {
        var f = output.Split('\x1f', 9);
        if (f.Length < 9 || f[0].Trim().Length == 0) return null;
        string message = f[8].Replace("\r\n", "\n").TrimEnd('\n', ' ');
        return new CommitInfo(f[0].Trim(), new GitPerson(f[1], f[2]), Time(f[3]), new GitPerson(f[4], f[5]), Time(f[6]),
            f[7].Split(' ', StringSplitOptions.RemoveEmptyEntries), message, CoAuthors(message));
    }

    /// <summary>The message's <c>Co-authored-by: Name &lt;email&gt;</c> trailers, in order, once each.</summary>
    public static IReadOnlyList<GitPerson> CoAuthors(string message)
    {
        var list = new List<GitPerson>();
        foreach (Match m in CoAuthorLine().Matches(message))
        {
            var p = new GitPerson(m.Groups[1].Value.Trim(), m.Groups[2].Value.Trim());
            if (!list.Any(x => string.Equals(x.Email, p.Email, StringComparison.OrdinalIgnoreCase))) list.Add(p);
        }
        return list;
    }

    /// <summary>
    /// A message as prose for a pane of any width: git convention wraps bodies at ~72 columns, which re-wrapped in a
    /// narrower or wider pane leaves ragged half-lines, so a wrapped line is joined back onto the one before it. Blank
    /// lines, list items, indented lines, quotes, trailers (<c>Key: value</c>) and fenced code keep their own lines.
    /// </summary>
    public static string Reflow(string message)
    {
        var lines = message.Replace("\r\n", "\n").Split('\n');
        var sb = new System.Text.StringBuilder(message.Length);
        bool fenced = false, joinable = false;
        foreach (var line in lines)
        {
            bool fence = line.TrimStart().StartsWith("```", StringComparison.Ordinal);
            bool keep = fenced || fence || line.Length == 0 || StandsAlone(line);
            if (sb.Length > 0)
                sb.Append(joinable && !keep ? ' ' : '\n');
            sb.Append(joinable && !keep ? line.TrimStart() : line);
            if (fence) fenced = !fenced;
            // The next line can carry on this one unless this one is blank, code (fenced or indented), or a
            // trailer; a list item's unindented wrap carries on the item.
            joinable = !fenced && !fence && line.Length > 0 && !char.IsWhiteSpace(line[0]) && !Trailer().IsMatch(line);
        }
        return sb.ToString();
    }

    // Lines that start something of their own rather than carrying on the sentence above.
    private static bool StandsAlone(string line) =>
        char.IsWhiteSpace(line[0]) || line[0] is '-' or '*' or '+' or '>' or '#' or '|' or '•'
        || ListNumber().IsMatch(line) || Trailer().IsMatch(line);

    [GeneratedRegex(@"^\d+[.)]\s")]
    private static partial Regex ListNumber();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9-]*:\s")]
    private static partial Regex Trailer();

    private static DateTimeOffset Time(string unix) =>
        long.TryParse(unix.Trim(), out var s) ? DateTimeOffset.FromUnixTimeSeconds(s) : DateTimeOffset.MinValue;

    [GeneratedRegex(@"^[ \t]*Co-authored-by:[ \t]*(.+?)[ \t]*<([^>\n]*)>[ \t]*$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex CoAuthorLine();
}

/// <summary>
/// Shorter names for refs whose real names are machine-made. GitHub's merge queue tests each PR on a branch named
/// <c>gh-readonly-queue/&lt;base&gt;/pr-&lt;n&gt;-&lt;sha&gt;</c>; what matters is which PR and where it's going.
/// </summary>
public static partial class RefLabel
{
    /// <summary>The PR number and target branch of a merge-queue branch (with or without its remote in front).</summary>
    public static (int Pr, string Base)? MergeQueue(string name) =>
        MergeQueueName().Match(name) is { Success: true } m && int.TryParse(m.Groups[2].Value, out int pr)
            ? (pr, m.Groups[1].Value) : null;

    /// <summary>What a pill or rail row shows: <c>queue #145 → main</c> for a merge-queue branch, else
    /// <paramref name="fallback"/> (the name as the caller would show it).</summary>
    public static string Display(string name, string fallback) =>
        MergeQueue(name) is { } q ? $"queue #{q.Pr} → {q.Base}" : fallback;

    [GeneratedRegex(@"(?:^|/)gh-readonly-queue/(.+)/pr-(\d+)-[0-9a-fA-F]+$")]
    private static partial Regex MergeQueueName();
}

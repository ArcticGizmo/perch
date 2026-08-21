namespace Perch.Plugins;

using System.Text.RegularExpressions;

/// <summary>
/// A GitHub install reference: <c>owner/repo</c>, optionally pinned to a version (<c>owner/repo@1.2.3</c>
/// or <c>@v1.2.3</c>). Parsing is strict — a malformed reference is rejected rather than turned into a
/// bogus API URL — and the pieces are recombined into the GitHub API endpoints the installer hits.
/// </summary>
internal sealed partial class PluginInstallSource
{
    [GeneratedRegex(@"^(?<owner>[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})?)/(?<repo>[A-Za-z0-9._-]{1,100})(?:@(?<ver>v?\d+\.\d+(?:\.\d+)?(?:-[0-9A-Za-z.-]+)?))?$")]
    private static partial Regex Ref();

    public string Owner { get; }
    public string Repo { get; }

    /// <summary>The pinned tag (with a leading <c>v</c>), or null to take the latest release.</summary>
    public string? Tag { get; }

    private PluginInstallSource(string owner, string repo, string? tag)
    {
        Owner = owner;
        Repo = repo;
        Tag = tag;
    }

    /// <summary>Parses <c>owner/repo</c> or <c>owner/repo@version</c>; returns null when malformed.</summary>
    public static PluginInstallSource? TryParse(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return null;
        var m = Ref().Match(reference.Trim());
        if (!m.Success) return null;

        string? tag = null;
        if (m.Groups["ver"].Success)
        {
            var v = m.Groups["ver"].Value;
            tag = v.StartsWith('v') ? v : "v" + v; // release tags are conventionally v-prefixed
        }
        return new PluginInstallSource(m.Groups["owner"].Value, m.Groups["repo"].Value, tag);
    }

    public string Slug => $"{Owner}/{Repo}";

    /// <summary>The GitHub API URL for the release to install: a specific tag, or "latest".</summary>
    public string ReleaseApiUrl => Tag is null
        ? $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest"
        : $"https://api.github.com/repos/{Owner}/{Repo}/releases/tags/{Tag}";
}

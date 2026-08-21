namespace Perch.Plugins;

using System.Text.Json;

/// <summary>A GitHub release as much of it as the installer needs: the tag, the human URL (for error
/// messages), and its downloadable assets.</summary>
internal sealed record GitHubRelease(string TagName, string HtmlUrl, IReadOnlyList<GitHubAsset> Assets)
{
    /// <summary>The single <c>.zip</c> payload asset, or null when there isn't exactly one (an empty or
    /// ambiguous release is a build error the installer surfaces rather than guessing).</summary>
    public GitHubAsset? FindPayloadZip()
    {
        var zips = Assets.Where(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)).ToList();
        return zips.Count == 1 ? zips[0] : null;
    }

    public GitHubAsset? FindAsset(string name) =>
        Assets.FirstOrDefault(a => a.Name == name);
}

internal sealed record GitHubAsset(string Name, string DownloadUrl);

/// <summary>Parses the GitHub Releases API JSON. Pure and defensive — a payload missing the fields it
/// needs comes back null rather than throwing — so the network layer stays a thin, untested seam.</summary>
internal static class GitHubReleaseParser
{
    public const string ChecksumsAssetName = "SHA256SUMS.txt";

    public static GitHubRelease? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var tag = Str(root, "tag_name");
            if (tag is null) return null;
            var html = Str(root, "html_url") ?? "";

            var assets = new List<GitHubAsset>();
            if (root.TryGetProperty("assets", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in arr.EnumerateArray())
                {
                    var name = Str(a, "name");
                    var url = Str(a, "browser_download_url");
                    if (name != null && url != null) assets.Add(new GitHubAsset(name, url));
                }
            }
            return new GitHubRelease(tag, html, assets);
        }
        catch (JsonException) { return null; }
    }

    private static string? Str(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;
}

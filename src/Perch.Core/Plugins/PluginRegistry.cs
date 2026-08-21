namespace Perch.Plugins;

/// <summary>
/// Discovers installed plugins by scanning the plugins directory — one sub-directory per plugin, each with
/// a <c>perch-plugin.json</c> at its root. Parse failures are captured per-plugin (never thrown) so one
/// broken manifest can't hide the rest, and a missing plugins directory is simply "no plugins".
/// </summary>
internal sealed class PluginRegistry
{
    public const string ManifestFileName = "perch-plugin.json";

    private readonly string _pluginsDir;
    private readonly string? _hostVersion;

    public PluginRegistry(string pluginsDir, string? hostVersion = null)
    {
        _pluginsDir = pluginsDir;
        _hostVersion = hostVersion;
    }

    /// <summary>Enumerates every plugin directory, parsing its manifest. Directories with no manifest are
    /// ignored; ones whose manifest fails to parse come back with <see cref="DiscoveredPlugin.Errors"/>
    /// populated and a null manifest.</summary>
    public IReadOnlyList<DiscoveredPlugin> Discover()
    {
        if (!Directory.Exists(_pluginsDir)) return [];

        var found = new List<DiscoveredPlugin>();
        foreach (var dir in Directory.EnumerateDirectories(_pluginsDir))
        {
            var manifestPath = Path.Combine(dir, ManifestFileName);
            if (!File.Exists(manifestPath)) continue;

            string json;
            try { json = File.ReadAllText(manifestPath); }
            catch (Exception ex)
            {
                found.Add(new DiscoveredPlugin(dir, null, [$"could not read {ManifestFileName}: {ex.Message}"]));
                continue;
            }

            var result = PluginManifestParser.Parse(json, _hostVersion);
            found.Add(new DiscoveredPlugin(dir, result.Manifest, result.Errors));
        }

        // Stable order (by id where known, else folder) so the overlay layout doesn't jitter run to run.
        return found
            .OrderBy(p => p.Manifest?.Id ?? Path.GetFileName(p.Directory), StringComparer.Ordinal)
            .ToList();
    }
}

/// <summary>A plugin directory found on disk: its folder, the parsed manifest (null if invalid), and any
/// validation errors.</summary>
internal sealed record DiscoveredPlugin(
    string Directory,
    PluginManifest? Manifest,
    IReadOnlyList<string> Errors)
{
    public bool Ok => Manifest != null && Errors.Count == 0;
    public string? Id => Manifest?.Id;
}

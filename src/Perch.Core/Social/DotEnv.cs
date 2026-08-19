namespace Perch.Social;

/// <summary>
/// A minimal <c>.env.local</c> reader for <b>dev builds</b>: finds the repo-root <c>.env.local</c> (the
/// directory that also holds <c>perch.slnx</c>) by walking up from the running binary, and parses its
/// <c>KEY=VALUE</c> lines. Scoping it to "the folder with perch.slnx" means it only ever fires in a checkout
/// — a shipped install under Program Files has no <c>perch.slnx</c> up its tree, so nothing is loaded there.
///
/// Deliberately tiny (no full dotenv semantics): <c>#</c> comments, blank lines and an optional
/// <c>export&#160;</c> prefix are handled, and a value may be wrapped in single or double quotes. It does not
/// do variable interpolation.
/// </summary>
public static class DotEnv
{
    /// <summary>The repo-root <c>.env.local</c> path (the directory alongside <c>perch.slnx</c>), or null if
    /// there's no such checkout above <paramref name="startDir"/> or the file isn't there.</summary>
    public static string? FindRepoEnvLocal(string startDir)
    {
        var dir = new DirectoryInfo(startDir);
        for (int i = 0; i < 12 && dir is not null; i++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "perch.slnx")))
            {
                var envFile = Path.Combine(dir.FullName, ".env.local");
                return File.Exists(envFile) ? envFile : null;
            }
        }
        return null;
    }

    /// <summary>Parses <c>KEY=VALUE</c> lines. Later duplicate keys win. Never throws on odd lines — they're
    /// skipped.</summary>
    public static Dictionary<string, string> Parse(string content)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in content.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            if (line.StartsWith("export ", StringComparison.Ordinal)) line = line["export ".Length..].TrimStart();

            int eq = line.IndexOf('=');
            if (eq <= 0) continue;

            var key = line[..eq].Trim();
            var val = line[(eq + 1)..].Trim();
            if (val.Length >= 2 &&
                ((val[0] == '"' && val[^1] == '"') || (val[0] == '\'' && val[^1] == '\'')))
                val = val[1..^1];

            if (key.Length > 0) map[key] = val;
        }
        return map;
    }
}

namespace Perch.Plugins;

using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>
/// Parses and strictly validates a <c>perch-plugin.json</c>. A manifest is an untrusted, third-party
/// document, so this is deliberately unforgiving: every unknown top-level (or capability) key, bad type,
/// or missing required field becomes a human-readable error rather than a silent default. Forward
/// compatibility rides on <see cref="PluginManifest.SupportedSchema"/>, not on tolerating stray keys.
/// Never throws — malformed input comes back as <see cref="PluginParseResult"/> with errors.
/// </summary>
internal static partial class PluginManifestParser
{
    private static readonly HashSet<string> KnownTopLevelKeys = new(StringComparer.Ordinal)
    {
        "schema", "id", "name", "version", "description", "author",
        "homepage", "minPerch", "entry", "extensionPoints", "capabilities",
    };

    private static readonly HashSet<string> KnownEntryKeys =
        new(StringComparer.Ordinal) { "type", "command", "args", "mode" };

    // Reverse-DNS-ish: lowercase segments of [a-z0-9-] joined by dots, at least two segments.
    [GeneratedRegex(@"^[a-z0-9]+(?:-[a-z0-9]+)*(?:\.[a-z0-9]+(?:-[a-z0-9]+)*)+$")]
    private static partial Regex IdPattern();

    /// <summary>Parses manifest JSON. <paramref name="hostVersion"/> gates the <c>minPerch</c> floor
    /// (pass null to skip that check, e.g. in unit tests).</summary>
    public static PluginParseResult Parse(string json, string? hostVersion = null)
    {
        var errors = new List<string>();

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
        }
        catch (JsonException ex)
        {
            return PluginParseResult.Failed($"not valid JSON: {ex.Message}");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return PluginParseResult.Failed("top level must be a JSON object.");

            foreach (var prop in root.EnumerateObject())
                if (!KnownTopLevelKeys.Contains(prop.Name))
                    errors.Add($"unknown top-level key '{prop.Name}'.");

            // schema — checked first; a future schema is rejected outright.
            int schema = 0;
            if (!TryGetInt(root, "schema", errors, required: true, out schema)) { /* error recorded */ }
            else if (schema != PluginManifest.SupportedSchema)
                errors.Add($"unsupported schema {schema} (this Perch understands schema {PluginManifest.SupportedSchema}).");

            var id = GetString(root, "id", errors, required: true);
            if (id != null && !IdPattern().IsMatch(id))
                errors.Add("id must be reverse-DNS style: lowercase letters, digits and hyphens in at least two dot-separated segments (e.g. dev.jon.weather).");

            var name = GetString(root, "name", errors, required: true);
            var version = GetString(root, "version", errors, required: true);
            if (version != null && !LooksLikeSemver(version))
                errors.Add($"version '{version}' is not a valid semver (expected e.g. 1.0.0).");

            var minPerch = GetString(root, "minPerch", errors, required: false);
            if (minPerch != null)
            {
                if (!LooksLikeSemver(minPerch))
                    errors.Add($"minPerch '{minPerch}' is not a valid semver.");
                else if (hostVersion != null && CompareSemver(hostVersion, minPerch) < 0)
                    errors.Add($"requires Perch {minPerch} or newer (this is {hostVersion}).");
            }

            var entry = ParseEntry(root, errors);
            var points = ParseExtensionPoints(root, errors);
            var caps = ParseCapabilities(root, errors);

            if (errors.Count > 0)
                return PluginParseResult.WithErrors(errors);

            return PluginParseResult.Success(new PluginManifest
            {
                Schema = schema,
                Id = id!,
                Name = name!,
                Version = version!,
                Description = GetString(root, "description", errors, required: false),
                Author = GetString(root, "author", errors, required: false),
                Homepage = GetString(root, "homepage", errors, required: false),
                MinPerch = minPerch,
                Entry = entry!,
                ExtensionPoints = points,
                Capabilities = caps,
            });
        }
    }

    private static PluginEntry? ParseEntry(JsonElement root, List<string> errors)
    {
        if (!root.TryGetProperty("entry", out var entry))
        {
            errors.Add("missing required key 'entry'.");
            return null;
        }
        if (entry.ValueKind != JsonValueKind.Object)
        {
            errors.Add("'entry' must be an object.");
            return null;
        }

        foreach (var prop in entry.EnumerateObject())
            if (!KnownEntryKeys.Contains(prop.Name))
                errors.Add($"unknown key 'entry.{prop.Name}'.");

        var type = GetString(entry, "type", errors, required: true, prefix: "entry.");
        if (type != null && type != PluginEntry.ProcessType)
            errors.Add($"entry.type '{type}' is not supported (only '{PluginEntry.ProcessType}').");

        var command = GetString(entry, "command", errors, required: true, prefix: "entry.");
        if (command != null && command.Trim().Length == 0)
            errors.Add("entry.command must not be blank.");

        var args = ParseStringArray(entry, "args", errors, prefix: "entry.");

        var mode = PluginEntryMode.OneShot;
        if (entry.TryGetProperty("mode", out var modeEl))
        {
            if (modeEl.ValueKind != JsonValueKind.String)
                errors.Add("entry.mode must be a string ('oneshot' or 'persistent').");
            else
            {
                mode = modeEl.GetString() switch
                {
                    "oneshot" => PluginEntryMode.OneShot,
                    "persistent" => PluginEntryMode.Persistent,
                    var other => Invalid(other),
                };
                PluginEntryMode Invalid(string? other)
                {
                    errors.Add($"entry.mode '{other}' is not valid (expected 'oneshot' or 'persistent').");
                    return PluginEntryMode.OneShot;
                }
            }
        }

        if (command == null || type == null) return null;
        return new PluginEntry { Type = type, Command = command, Args = args, Mode = mode };
    }

    private static IReadOnlyList<string> ParseExtensionPoints(JsonElement root, List<string> errors)
    {
        if (!root.TryGetProperty("extensionPoints", out var el))
        {
            errors.Add("missing required key 'extensionPoints'.");
            return [];
        }
        if (el.ValueKind != JsonValueKind.Array)
        {
            errors.Add("'extensionPoints' must be an array.");
            return [];
        }

        var points = new List<string>();
        foreach (var item in el.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) { errors.Add("extensionPoints entries must be strings."); continue; }
            var p = item.GetString()!;
            if (!PluginExtensionPoints.All.Contains(p))
                errors.Add($"unknown extension point '{p}'. Known: {string.Join(", ", PluginExtensionPoints.All)}.");
            else if (points.Contains(p))
                errors.Add($"duplicate extension point '{p}'.");
            else
                points.Add(p);
        }

        if (points.Count == 0 && !errors.Any(e => e.Contains("extension point")))
            errors.Add("extensionPoints must list at least one point.");
        return points;
    }

    private static PluginCapabilities ParseCapabilities(JsonElement root, List<string> errors)
    {
        if (!root.TryGetProperty("capabilities", out var el))
            return new PluginCapabilities();

        if (el.ValueKind != JsonValueKind.Object)
        {
            errors.Add("'capabilities' must be an object.");
            return new PluginCapabilities();
        }

        foreach (var prop in el.EnumerateObject())
            if (!PluginCapabilityKeys.All.Contains(prop.Name))
                errors.Add($"unknown capability '{prop.Name}'.");

        var network = ParseStringArray(el, PluginCapabilityKeys.Network, errors, prefix: "capabilities.");
        // Normalise: trim, drop blanks, lowercase (hostnames are case-insensitive).
        network = network.Select(h => h.Trim().ToLowerInvariant()).Where(h => h.Length > 0).Distinct().ToArray();

        int interval = PluginCapabilities.DefaultPollIntervalSec;
        if (el.TryGetProperty(PluginCapabilityKeys.PollIntervalSec, out var iv))
        {
            if (iv.ValueKind != JsonValueKind.Number || !iv.TryGetInt32(out interval))
                errors.Add("capabilities.'poll.intervalSec' must be an integer.");
            else if (interval < PluginCapabilities.MinPollIntervalSec)
                interval = PluginCapabilities.MinPollIntervalSec; // clamp, not an error
        }

        return new PluginCapabilities
        {
            Network = network,
            ReadSessions = GetBool(el, PluginCapabilityKeys.ReadSessions, errors, prefix: "capabilities."),
            ReadCwd = GetBool(el, PluginCapabilityKeys.ReadCwd, errors, prefix: "capabilities."),
            Notify = GetBool(el, PluginCapabilityKeys.Notify, errors, prefix: "capabilities."),
            PollIntervalSec = interval,
        };
    }

    // ── small typed getters (record an error rather than throw) ──────────────────────────
    private static string? GetString(JsonElement obj, string key, List<string> errors, bool required, string prefix = "")
    {
        if (!obj.TryGetProperty(key, out var el))
        {
            if (required) errors.Add($"missing required key '{prefix}{key}'.");
            return null;
        }
        if (el.ValueKind != JsonValueKind.String)
        {
            errors.Add($"'{prefix}{key}' must be a string.");
            return null;
        }
        var s = el.GetString();
        if (required && string.IsNullOrWhiteSpace(s))
        {
            errors.Add($"'{prefix}{key}' must not be blank.");
            return null;
        }
        return s;
    }

    private static bool GetBool(JsonElement obj, string key, List<string> errors, string prefix = "")
    {
        if (!obj.TryGetProperty(key, out var el)) return false;
        if (el.ValueKind == JsonValueKind.True) return true;
        if (el.ValueKind == JsonValueKind.False) return false;
        errors.Add($"'{prefix}{key}' must be a boolean.");
        return false;
    }

    private static bool TryGetInt(JsonElement obj, string key, List<string> errors, bool required, out int value)
    {
        value = 0;
        if (!obj.TryGetProperty(key, out var el))
        {
            if (required) errors.Add($"missing required key '{key}'.");
            return false;
        }
        if (el.ValueKind != JsonValueKind.Number || !el.TryGetInt32(out value))
        {
            errors.Add($"'{key}' must be an integer.");
            return false;
        }
        return true;
    }

    private static IReadOnlyList<string> ParseStringArray(JsonElement obj, string key, List<string> errors, string prefix = "")
    {
        if (!obj.TryGetProperty(key, out var el)) return [];
        if (el.ValueKind != JsonValueKind.Array)
        {
            errors.Add($"'{prefix}{key}' must be an array of strings.");
            return [];
        }
        var list = new List<string>();
        foreach (var item in el.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) { errors.Add($"'{prefix}{key}' entries must be strings."); continue; }
            list.Add(item.GetString()!);
        }
        return list;
    }

    // ── minimal semver (major.minor.patch, optional -prerelease) — enough to compare a host floor ──
    private static bool LooksLikeSemver(string v)
    {
        var core = v.Split('-', 2)[0];
        var parts = core.Split('.');
        return parts.Length is 2 or 3 && parts.All(p => p.Length > 0 && p.All(char.IsDigit));
    }

    /// <summary>Compares numeric major.minor.patch. Prerelease tags are ignored (a floor check only cares
    /// about the release line). Returns &lt;0 if a &lt; b, 0 if equal, &gt;0 if a &gt; b.</summary>
    internal static int CompareSemver(string a, string b)
    {
        static int[] Nums(string v) => v.Split('-', 2)[0].Split('.')
            .Select(p => int.TryParse(p, out var n) ? n : 0).ToArray();
        var x = Nums(a); var y = Nums(b);
        for (int i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            int xi = i < x.Length ? x[i] : 0, yi = i < y.Length ? y[i] : 0;
            if (xi != yi) return xi.CompareTo(yi);
        }
        return 0;
    }
}

/// <summary>The outcome of parsing a manifest: a validated <see cref="PluginManifest"/> or a list of
/// human-readable errors (never both meaningfully populated).</summary>
internal sealed class PluginParseResult
{
    public PluginManifest? Manifest { get; private init; }
    public IReadOnlyList<string> Errors { get; private init; } = [];
    public bool Ok => Manifest != null && Errors.Count == 0;

    public static PluginParseResult Success(PluginManifest m) => new() { Manifest = m };
    public static PluginParseResult Failed(string error) => new() { Errors = [error] };
    public static PluginParseResult WithErrors(IReadOnlyList<string> errors) => new() { Errors = errors };
}

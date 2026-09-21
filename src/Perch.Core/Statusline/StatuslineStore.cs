namespace Perch.Statusline;

using System.IO;
using System.Text.Json;
using Perch.Data;

/// <summary>
/// Loads and saves the statusline profile library (<c>statusline.json</c> in Perch's per-profile
/// app-data folder — the same <c>%APPDATA%/Perch[…]</c> root as <c>settings.json</c>). Best-effort:
/// a missing or unreadable file loads as the seeded default set, and any write failure is swallowed.
/// The path-taking overloads are the test seam (nothing touches the shared app-data file).
/// </summary>
internal static class StatuslineStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,   // tolerate a hand-edited statusline.json
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static string DefaultPath => Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
        AppProfile.DataFolderName, "statusline.json");

    public static StatuslineConfig Load() => Load(DefaultPath);

    public static StatuslineConfig Load(string path)
    {
        try
        {
            if (File.Exists(path)
                && JsonSerializer.Deserialize<StatuslineConfig>(File.ReadAllText(path), Options) is { } cfg
                && cfg.Profiles.Count > 0)
            {
                cfg.ActiveName ??= cfg.Profiles[0].Name;
                return cfg;
            }
        }
        catch { /* fall through to a fresh seeded config */ }
        return Seeded();
    }

    public static bool Save(StatuslineConfig config) => Save(DefaultPath, config);

    public static bool Save(string path, StatuslineConfig config)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(config, Options));
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>A brand-new config carrying the built-in Perch templates, with the first one active.</summary>
    public static StatuslineConfig Seeded()
    {
        var cfg = new StatuslineConfig();
        cfg.Profiles.AddRange(StatuslineDefaults.All);
        cfg.ActiveName = StatuslineDefaults.PerchDefaultName;
        return cfg;
    }
}

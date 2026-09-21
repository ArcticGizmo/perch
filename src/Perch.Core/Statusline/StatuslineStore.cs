namespace Perch.Statusline;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Perch.Data;

/// <summary>
/// The statusline profile library. The built-in examples are the code in <see cref="StatuslineDefaults"/>
/// and are the source of truth: they always appear, and adding a new one in code makes it show up with no
/// migration. The on-disk <c>statusline.json</c> (in Perch's per-profile app-data folder) holds only what
/// the user has actually changed — their own created/imported profiles, any built-in they've <em>edited</em>
/// (an override), and which profile is active. <see cref="Load"/> merges the two; <see cref="Save"/> writes
/// back only those deltas, so an unedited built-in keeps tracking the code.
///
/// <para>Best-effort: a missing or unreadable file just yields the built-ins. The path-taking overloads
/// are the test seam (nothing touches the shared app-data file).</para>
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
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        AppProfile.DataFolderName, "statusline.json");

    // Mirrors AppSettings.DisablePersistence: the headless `render` process poses the designer window and
    // closes it, which would otherwise Save over a user's real statusline.json. Render calls this so every
    // default-path Save becomes a no-op. The path-taking Save overload is unaffected (the test seam).
    private static bool _persistenceDisabled;
    public static void DisablePersistence() => _persistenceDisabled = true;

    public static StatuslineConfig Load() => Load(DefaultPath);

    public static StatuslineConfig Load(string path)
    {
        StatuslineConfig? saved = null;
        try
        {
            if (File.Exists(path))
                saved = JsonSerializer.Deserialize<StatuslineConfig>(File.ReadAllText(path), Options);
        }
        catch { saved = null; }
        return Merge(saved);
    }

    /// <summary>The library with no saved file — just the code built-ins.</summary>
    public static StatuslineConfig Seeded() => Merge(null);

    // Effective library = code built-ins (each overridden by a saved profile of the same name only when the
    // saved one *differs*), then the user's own saved profiles in their saved order. So a brand-new code
    // built-in appears automatically, an unedited built-in always tracks the code, and an edited one keeps
    // the user's version.
    private static StatuslineConfig Merge(StatuslineConfig? saved)
    {
        var savedProfiles = saved?.Profiles ?? new List<StatuslineProfile>();
        var builtinNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new StatuslineConfig();

        foreach (var def in StatuslineDefaults.All)
        {
            def.Builtin = true;
            builtinNames.Add(def.Name);
            var overridden = savedProfiles.FirstOrDefault(p => NameEq(p.Name, def.Name));
            if (overridden is not null && !overridden.SameContentAs(def))
            {
                overridden.Builtin = true;
                result.Profiles.Add(overridden);
            }
            else
            {
                result.Profiles.Add(def);
            }
        }

        foreach (var p in savedProfiles)
        {
            if (builtinNames.Contains(p.Name)) continue;   // built-in override already placed above
            p.Builtin = false;
            result.Profiles.Add(p);
        }

        result.ActiveName = saved?.ActiveName is { } a && result.Find(a) is not null
            ? a
            : result.Profiles.FirstOrDefault()?.Name;
        return result;
    }

    public static bool Save(StatuslineConfig config)
    {
        if (_persistenceDisabled) return true;   // render/tests: never touch the real statusline.json
        return Save(DefaultPath, config);
    }

    public static bool Save(string path, StatuslineConfig config)
    {
        try
        {
            // Persist only deltas from code: user profiles, and built-ins the user has edited. An unedited
            // built-in is skipped entirely, so it always comes from StatuslineDefaults on the next load.
            var toSave = new StatuslineConfig { ActiveName = config.ActiveName };
            foreach (var p in config.Profiles)
            {
                var def = StatuslineDefaults.All.FirstOrDefault(d => NameEq(d.Name, p.Name));
                if (def is not null && p.SameContentAs(def)) continue;   // untouched built-in — lives in code
                toSave.Profiles.Add(p);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(toSave, Options));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool NameEq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

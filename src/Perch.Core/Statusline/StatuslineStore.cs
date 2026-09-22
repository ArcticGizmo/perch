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

    // Effective library = the code built-ins (always, verbatim from StatuslineDefaults), then the user's own
    // saved profiles in their saved order. Built-in templates are CODE-FIRST: they're never read from disk,
    // so a brand-new code example appears automatically and an existing one always tracks the code. A saved
    // profile whose name matches a built-in is a stale/legacy entry (built-ins are read-only in the UI, so no
    // legitimate same-name override can exist) and is dropped — the code built-in owns that name.
    private static StatuslineConfig Merge(StatuslineConfig? saved)
    {
        var savedProfiles = saved?.Profiles ?? new List<StatuslineProfile>();
        var builtinNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new StatuslineConfig();

        foreach (var def in StatuslineDefaults.All)
        {
            def.Builtin = true;
            builtinNames.Add(def.Name);
            result.Profiles.Add(def);
        }

        foreach (var p in savedProfiles)
        {
            if (builtinNames.Contains(p.Name)) continue;   // a built-in owns this name — ignore the disk copy
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
            // Code-first: only the user's OWN profiles ever touch disk. A built-in template lives solely in
            // StatuslineDefaults and is never persisted — not even the active one, and not even if it was
            // mutated in memory — so it can never drift from the code or leave a stale copy behind. Anything
            // carrying a built-in name is therefore skipped (built-in names are reserved for code).
            var builtinNames = new HashSet<string>(StatuslineDefaults.All.Select(d => d.Name), StringComparer.OrdinalIgnoreCase);
            var toSave = new StatuslineConfig { ActiveName = config.ActiveName };
            foreach (var p in config.Profiles)
                if (!builtinNames.Contains(p.Name)) toSave.Profiles.Add(p);

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(toSave, Options));
            return true;
        }
        catch
        {
            return false;
        }
    }
}

namespace Perch.Theming;

/// <summary>
/// Resolves a theme id against the full catalogue — the built-in presets plus the user's custom themes
/// (persisted in settings). The one place that knows "all themes the app can show", so the picker,
/// startup and the runtime swap all agree.
/// </summary>
public static class ThemeCatalog
{
    // Head-contributed themes harvested from an external palette library (the ArcticGizmo palette package;
    // see Perch.App's PaletteImport). Registered once at startup, before the active theme is resolved. They
    // behave like built-ins — shown in the picker, not editable/deletable, and never written to
    // AppSettings.CustomThemes — so they can't reference the (UI-bearing) package from here: the head maps
    // them to Perch Theme records and hands them in.
    private static IReadOnlyList<Theme> _imported = [];

    /// <summary>The head-contributed imported themes (empty until <see cref="RegisterImported"/> runs).</summary>
    public static IReadOnlyList<Theme> Imported => _imported;

    /// <summary>Register the imported themes contributed by the app head. Idempotent; call once at startup
    /// before the active theme is resolved (and before a headless render).</summary>
    public static void RegisterImported(IEnumerable<Theme>? themes) =>
        _imported = themes?.ToArray() ?? [];

    /// <summary>Built-ins first, then the imported presets, then the user's custom themes (if any).</summary>
    public static IEnumerable<Theme> All(IEnumerable<Theme>? custom)
    {
        var builtInLike = Themes.BuiltIn.Concat(_imported);
        return custom is null ? builtInLike : builtInLike.Concat(custom);
    }

    /// <summary>The theme for <paramref name="id"/> across built-ins + imported + <paramref name="custom"/>,
    /// or Midnight when the id is unknown/missing (so the app is never left uncoloured).</summary>
    public static Theme Resolve(string? id, IEnumerable<Theme>? custom)
    {
        if (id is not null)
            foreach (var t in All(custom))
                if (t.Id == id) return t;
        return Themes.Midnight;
    }

    /// <summary>True when <paramref name="id"/> names a built-in or imported preset (neither of which can be
    /// edited/deleted by the user).</summary>
    public static bool IsBuiltIn(string? id) =>
        Themes.ById(id) is not null || (id is not null && _imported.Any(t => t.Id == id));
}

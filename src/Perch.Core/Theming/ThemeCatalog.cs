namespace Perch.Theming;

/// <summary>
/// Resolves a theme id against the full catalogue — the built-in presets plus the user's custom themes
/// (persisted in settings). The one place that knows "all themes the app can show", so the picker,
/// startup and the runtime swap all agree.
/// </summary>
public static class ThemeCatalog
{
    /// <summary>Built-ins first, then the user's custom themes (if any).</summary>
    public static IEnumerable<Theme> All(IEnumerable<Theme>? custom) =>
        custom is null ? Themes.BuiltIn : Themes.BuiltIn.Concat(custom);

    /// <summary>The theme for <paramref name="id"/> across built-ins + <paramref name="custom"/>, or
    /// Midnight when the id is unknown/missing (so the app is never left uncoloured).</summary>
    public static Theme Resolve(string? id, IEnumerable<Theme>? custom)
    {
        if (id is not null)
            foreach (var t in All(custom))
                if (t.Id == id) return t;
        return Themes.Midnight;
    }

    /// <summary>True when <paramref name="id"/> names a built-in preset (which can't be edited/deleted).</summary>
    public static bool IsBuiltIn(string? id) => Themes.ById(id) is not null;
}

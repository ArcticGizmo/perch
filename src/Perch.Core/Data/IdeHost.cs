namespace Perch.Data;

/// <summary>
/// Which editor/IDE is hosting a session, when it runs inside one (or inside a terminal embedded in one).
/// Drives the overlay's per-brand origin glyph. <see cref="Other"/> covers a recognised-as-IDE host we
/// don't draw a bespoke mark for (Zed, Visual Studio, an unknown JetBrains product, …) — it still gets the
/// generic editor glyph.
/// </summary>
public enum IdeHostKind
{
    VsCode,
    Cursor,
    Windsurf,
    JetBrains,
    Other,
}

/// <summary>
/// A session's host editor/IDE, resolved from its process ancestry (a terminal running under an IDE
/// process, or the IDE's own integrated terminal). <see cref="Kind"/> selects the glyph; <see cref="DisplayName"/>
/// is the human label ("Visual Studio Code", "PyCharm", …) shown in the tooltip; <see cref="Executable"/> is the
/// ancestor process's executable — its full path when the detector could resolve it (so the app icon can be
/// rendered from the real binary via <see cref="Perch.Platform.IAppIconProvider"/>), otherwise just the
/// base name it was matched from.
///
/// <para>Detection is a platform capability — see <see cref="Perch.Platform.IIdeHostDetector"/> — but the
/// executable→host mapping (<see cref="FromExecutable"/>) is pure and lives here so every head and the tests
/// share one table.</para>
/// </summary>
public sealed record IdeHost(IdeHostKind Kind, string DisplayName, string? Executable = null)
{
    /// <summary>
    /// Map a process's executable file name to the IDE it identifies, or null when it isn't a known IDE.
    /// Accepts the raw name (with or without a <c>.exe</c> suffix, any case); an ancestry walker calls this
    /// on each ancestor and takes the first non-null result. Deliberately conservative — a plain shell,
    /// Windows Terminal, or a generic process returns null so only a real IDE host lights the glyph.
    /// </summary>
    public static IdeHost? FromExecutable(string? exeFileName)
    {
        if (string.IsNullOrWhiteSpace(exeFileName))
            return null;

        // Normalise: strip any directory, drop a trailing ".exe", lower-case.
        var name = exeFileName.Trim();
        int slash = name.LastIndexOfAny(['\\', '/']);
        if (slash >= 0)
            name = name[(slash + 1)..];
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        name = name.ToLowerInvariant();
        if (name.Length == 0)
            return null;

        // VS Code and its distributions. The Insiders build ships as "Code - Insiders"; the open-source
        // rebuilds as "VSCodium"/"code-oss". All share the VS Code glyph.
        switch (name)
        {
            case "code":
                return new IdeHost(IdeHostKind.VsCode, "Visual Studio Code", name);
            case "code - insiders":
                return new IdeHost(IdeHostKind.VsCode, "VS Code (Insiders)", name);
            case "codium":
            case "vscodium":
            case "code-oss":
                return new IdeHost(IdeHostKind.VsCode, "VSCodium", name);
            case "cursor":
                return new IdeHost(IdeHostKind.Cursor, "Cursor", name);
            case "windsurf":
                return new IdeHost(IdeHostKind.Windsurf, "Windsurf", name);
            case "zed":
                return new IdeHost(IdeHostKind.Other, "Zed", name);
            case "devenv":
                return new IdeHost(IdeHostKind.Other, "Visual Studio", name);
        }

        // JetBrains IDEs. The launcher executables carry the product name, usually with a "64" arch suffix
        // (idea64.exe, pycharm64.exe, …) — match by prefix so both the plain and "64" forms resolve. They
        // all share the JetBrains glyph; the product only refines the tooltip label.
        foreach (var (prefix, product) in JetBrainsProducts)
            if (name.StartsWith(prefix, StringComparison.Ordinal))
                return new IdeHost(IdeHostKind.JetBrains, product, name);

        return null;
    }

    // Ordered longest-first so a more specific prefix can't be shadowed by a shorter one (none currently
    // overlap, but keeping the invariant makes adding products safe).
    private static readonly (string Prefix, string Product)[] JetBrainsProducts =
    [
        ("idea",      "IntelliJ IDEA"),
        ("pycharm",   "PyCharm"),
        ("webstorm",  "WebStorm"),
        ("phpstorm",  "PhpStorm"),
        ("rubymine",  "RubyMine"),
        ("rustrover", "RustRover"),
        ("dataspell", "DataSpell"),
        ("datagrip",  "DataGrip"),
        ("goland",    "GoLand"),
        ("clion",     "CLion"),
        ("rider",     "Rider"),
        ("aqua",      "Aqua"),
        ("fleet",     "Fleet"),
    ];
}

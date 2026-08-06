namespace Perch.Data;

/// <summary>
/// The single place that turns a platform capture identity into something human. Everything else — the platform
/// monitors, the host, the overlay strip — stays entirely product-agnostic and asks here.
///
/// <para>It used to recognise <em>which</em> product was calling, so a Teams-specific control layer could be
/// layered on top. That layer is gone (see the remarks on the overlay's mic strip), and with it the last place
/// in the microphone path that treated one app differently from another. All that's left is naming, which is
/// cosmetic.</para>
///
/// Pure string logic with no OS calls, so it is unit-testable and shared by every head. Identities arrive
/// in the two shapes Windows uses (a package family name such as <c>MSTeams_8wekyb3d8bbwe</c>, or a full
/// executable path) and the helpers below cope with either; a macOS head can pass a bundle id and get
/// sensible fallbacks without changing anything here.
/// </summary>
public static class MicApps
{
    // Well-known identity tokens → the name people actually call the app. Matched against the token
    // extracted from an identity (see Token), so one entry covers both the packaged and the plain-exe form
    // of the same product. Only for apps whose derived name would otherwise be wrong or ugly — an unknown
    // app falls through to the generic prettifier rather than needing an entry here.
    private static readonly (string Token, string Name)[] KnownNames =
    [
        ("msteams",         "Microsoft Teams"),
        ("ms-teams",        "Microsoft Teams"),
        ("microsoftteams",  "Microsoft Teams"),
        ("teams",           "Microsoft Teams"),
        ("slack",           "Slack"),
        ("zoom",            "Zoom"),
        ("cpthost",         "Zoom"),          // Zoom's meeting host process
        ("discord",         "Discord"),
        ("webexmta",        "Webex"),
        ("webex",           "Webex"),
        ("chrome",          "Google Chrome"),
        ("msedge",          "Microsoft Edge"),
        ("firefox",         "Firefox"),
        ("obs64",           "OBS Studio"),
        ("obs32",           "OBS Studio"),
        ("audacity",        "Audacity"),
    ];

    /// <summary>
    /// The best human-readable name for a capture identity. <paramref name="fileDescription"/> is the
    /// platform's own description when one could be read (on Windows, the executable's version-info
    /// <c>FileDescription</c> — "Microsoft Teams", "Google Chrome"); it wins when present because it is
    /// the app's own answer. Otherwise a small known-app table, then a generic prettifier. Never returns
    /// an empty string for a non-empty identity, so the UI always has something to draw.
    /// </summary>
    public static string DisplayName(string? identity, string? fileDescription = null)
    {
        if (!string.IsNullOrWhiteSpace(fileDescription)) return fileDescription.Trim();

        var token = Token(identity);
        foreach (var (known, name) in KnownNames)
            if (token.Equals(known, StringComparison.OrdinalIgnoreCase)) return name;

        return token.Length == 0 ? "Unknown app" : Prettify(token);
    }

    /// <summary>
    /// Reduces an identity to its comparable token: the executable's base name for a path, or the package
    /// name shorn of its publisher prefix and publisher hash for a package family name. So
    /// <c>MSTeams_8wekyb3d8bbwe</c> → <c>MSTeams</c>, <c>91750D7E.Slack_8she8kybcnzg4</c> → <c>Slack</c>,
    /// and <c>C:\…\Application\chrome.exe</c> → <c>chrome</c>. Returns "" for a null/blank identity.
    /// </summary>
    public static string Token(string? identity)
    {
        if (string.IsNullOrWhiteSpace(identity)) return "";
        var s = identity.Trim();

        // A path (either separator, and the ConsentStore's '#'-mangled form is un-mangled before it
        // reaches here) → the file name without its extension.
        if (s.Contains('\\') || s.Contains('/'))
        {
            var slash = s.LastIndexOfAny(['\\', '/']);
            s = s[(slash + 1)..];
            var dot = s.LastIndexOf('.');
            if (dot > 0) s = s[..dot];
            return s;
        }

        // A bare executable name that arrived without any path ("ms-teams.exe"). Handled before the package
        // logic below, which would otherwise read the ".exe" as a package's publisher-prefix separator and
        // reduce the whole thing to "exe".
        if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return s[..^4];

        // A package family name: "<publisher-prefix>.<name>_<publisherId>". Drop the hash, then the prefix.
        var underscore = s.LastIndexOf('_');
        if (underscore > 0) s = s[..underscore];
        var lastDot = s.LastIndexOf('.');
        if (lastDot >= 0 && lastDot < s.Length - 1) s = s[(lastDot + 1)..];
        return s;
    }

    /// <summary>
    /// Whether a capture identity refers to the app running from <paramref name="path"/> — the join that lets
    /// a platform match an identity it got from a privacy/consent ledger against a live process it got from
    /// the audio stack. Paths compare directly. A package family name
    /// (<c>MSTeams_8wekyb3d8bbwe</c> = <c>{name}_{publisherId}</c>) is matched against the install folder in
    /// the path (<c>…\WindowsApps\MSTeams_26163.405.4842.717_x64__8wekyb3d8bbwe\ms-teams.exe</c>), which
    /// carries both parts around the version and architecture. Requiring <em>both</em> is what stops a
    /// same-named package from a different publisher matching.
    /// </summary>
    public static bool IdentityMatchesPath(string? identity, string? path)
    {
        if (string.IsNullOrEmpty(identity) || string.IsNullOrEmpty(path)) return false;

        if (identity.Contains('\\') || identity.Contains('/'))
            return string.Equals(identity, path, StringComparison.OrdinalIgnoreCase);

        var underscore = identity.LastIndexOf('_');
        if (underscore <= 0 || underscore == identity.Length - 1) return false;
        var packageName = identity[..underscore];
        var publisherId = identity[(underscore + 1)..];

        return path.Contains($@"\{packageName}_", StringComparison.OrdinalIgnoreCase)
            && path.Contains($@"__{publisherId}\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether two capture/source identities name the same app, compared by their reduced <see cref="Token"/>.
    /// The join that lets the overlay tell that a media session's source app is the same product as an app
    /// holding the microphone — a media <c>SourceAppUserModelId</c> such as <c>MSTeams_8wekyb3d8bbwe!App</c>
    /// and a mic identity <c>MSTeams_8wekyb3d8bbwe</c> both reduce to the token <c>MSTeams</c>. Blank or
    /// tokenless identities never match, so a source that doesn't report an app id can't suppress anything.
    /// </summary>
    public static bool IsSameApp(string? identityA, string? identityB)
    {
        var a = Token(identityA);
        return a.Length > 0 && a.Equals(Token(identityB), StringComparison.OrdinalIgnoreCase);
    }

    // Last-resort name for an app with no version info and no table entry: split a CamelCase/kebab token
    // into words and capitalise. "voicemeeter-vban" -> "Voicemeeter Vban", "SoundRecorder" -> "Sound Recorder".
    private static string Prettify(string token)
    {
        var words = new List<string>();
        var current = new System.Text.StringBuilder();
        foreach (var ch in token)
        {
            if (ch is '-' or '_' or '.' or ' ')
            {
                if (current.Length > 0) { words.Add(current.ToString()); current.Clear(); }
                continue;
            }
            // Start a new word at a lower→upper transition, so CamelCase splits, but keep runs of capitals
            // together so an acronym ("MSTeams", "OBS") isn't shattered into single letters.
            if (char.IsUpper(ch) && current.Length > 0 && char.IsLower(current[^1]))
            {
                words.Add(current.ToString());
                current.Clear();
            }
            current.Append(ch);
        }
        if (current.Length > 0) words.Add(current.ToString());

        return string.Join(' ', words.Select(w => char.IsLower(w[0]) ? char.ToUpperInvariant(w[0]) + w[1..] : w));
    }
}

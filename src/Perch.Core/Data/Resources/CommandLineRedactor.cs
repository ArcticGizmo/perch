using System.Text.RegularExpressions;

namespace Perch.Data.Resources;

/// <summary>
/// Scrubs a command line before a resource recording stores it. Recordings are meant to be shared ("look what
/// Docker did"), and command lines routinely carry tokens and the user's home path, so this errs towards
/// over-redacting: a lost argument costs nothing, a leaked token does.
/// </summary>
public static partial class CommandLineRedactor
{
    public const int MaxLength = 1024;
    private const string Mask = "***";

    public static string Redact(string commandLine) =>
        Redact(commandLine, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    public static string Redact(string commandLine, string? userProfile)
    {
        if (string.IsNullOrEmpty(commandLine)) return "";
        string s = commandLine;

        if (ProfilePattern(userProfile) is { } profile)
            s = profile.Replace(s, "~");

        s = UrlUserInfo().Replace(s, m => m.Groups[1].Value + Mask + "@");
        s = Bearer().Replace(s, m => m.Groups[1].Value + Mask);
        s = SecretFlag().Replace(s, m => m.Groups[1].Value + m.Groups[2].Value + Mask);
        s = SecretAssignment().Replace(s, m => m.Groups[1].Value + "=" + Mask);
        s = LongRun().Replace(s, m => LooksLikeSecret(m.Value) ? Mask : m.Value);

        return s.Length <= MaxLength ? s : s[..MaxLength] + "…";
    }

    private static (string? profile, Regex? pattern) _profileCache;

    // The profile folder in every spelling a command line uses: C:\Users\x, C:/Users/x, C:\\Users\\x (escaped inside
    // an argument), and /c/Users/x (Git Bash), which is how hook and status-line commands reach a recording.
    private static Regex? ProfilePattern(string? userProfile)
    {
        var trimmed = userProfile?.TrimEnd('\\', '/');
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length <= 3) return null;
        var cache = _profileCache;
        if (cache.profile == trimmed) return cache.pattern;

        var parts = trimmed.Split('\\', '/', StringSplitOptions.RemoveEmptyEntries);
        string head;
        IEnumerable<string> rest;
        if (parts[0] is [var drive, ':'])
        {
            head = $"(?:{Regex.Escape(parts[0])}|/{char.ToLowerInvariant(drive)})";
            rest = parts.Skip(1);
        }
        else
        {
            head = "";
            rest = parts;
        }
        var body = string.Concat(rest.Select(p => @"[\\/]{1,2}" + Regex.Escape(p)));
        var pattern = new Regex(head + body + @"(?![\w.-])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        _profileCache = (trimmed, pattern);
        return pattern;
    }

    // A long unbroken run mixing letters and digits reads as a key or token. A GUID is an identifier, not a
    // secret, and is worth keeping: it tells two otherwise identical commands apart. So is a kebab/snake name made
    // of short parts ("20261106-085017-resource-monitor"); a token has at least one long opaque part ("ghp_…").
    private static bool LooksLikeSecret(string run) =>
        !Guid.TryParse(run, out _)
        && run.Any(char.IsDigit) && run.Any(char.IsLetter)
        && run.Split('-', '_').Any(part => part.Length > 16);

    private const string SecretWords = "token|secret|password|passwd|pwd|apikey|api-key|api_key|key|auth|bearer|credential|cookie";

    [GeneratedRegex(@"([a-z][a-z0-9+.-]*://)[^/\s:@]+:[^/\s@]+@", RegexOptions.IgnoreCase)]
    private static partial Regex UrlUserInfo();

    [GeneratedRegex(@"\b(bearer\s+)(?!\*\*\*)\S+", RegexOptions.IgnoreCase)]
    private static partial Regex Bearer();

    // --token abc · --api-key=abc · -password "a b" · /auth:abc. Anchored to the start of an argument so a
    // directory such as C:\oauth\server.js isn't read as a switch.
    [GeneratedRegex(@"(?<=^|\s)((?:--?|/)[\w.-]*(?:" + SecretWords + @")[\w.-]*)(\s*[=:]\s*|\s+)(""[^""]*""|'[^']*'|[^\s""']+)",
        RegexOptions.IgnoreCase)]
    private static partial Regex SecretFlag();

    // GITHUB_TOKEN=abc · db.password=abc
    [GeneratedRegex(@"(?<=^|\s)([A-Za-z_][\w.]*(?:" + SecretWords + @")[\w.]*)=(?!\*\*\*)(""[^""]*""|'[^']*'|[^\s""']+)",
        RegexOptions.IgnoreCase)]
    private static partial Regex SecretAssignment();

    // No '/' in the class, so a long POSIX path is never one run.
    [GeneratedRegex(@"[A-Za-z0-9+_-]{32,}={0,2}")]
    private static partial Regex LongRun();
}

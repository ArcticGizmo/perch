using System.Collections.Concurrent;

namespace Perch.Data;

/// <summary>
/// Resolves a bare command name (<c>git</c>, <c>claude</c>, <c>code</c>) to an absolute executable path by walking
/// <c>PATH</c> (+ <c>PATHEXT</c> on Windows) — and <b>never the current directory</b>.
///
/// <para>Why this exists: every Windows way of starting a bare name searches a working directory before PATH.
/// <c>CreateProcess</c> tries the parent's current directory, <c>cmd.exe</c> its own current directory,
/// <c>ShellExecute</c> its <c>lpDirectory</c>; .NET on Unix checks the current directory before PATH as well. Perch
/// runs things inside repositories it didn't write, so a <c>git.exe</c> or <c>claude.cmd</c> committed to one would
/// run in place of the real tool. Resolving to an absolute path up front takes every one of those searches out of
/// play. See docs/review-fixes-plan.md CP7.</para>
///
/// <para>Relative PATH entries (<c>.</c>, <c>bin</c>) are skipped for the same reason: they resolve against the
/// current directory. Results are cached; a hit is re-validated with a cheap existence check, and a miss is retried
/// after <see cref="MissRetry"/> (so installing a tool while Perch runs is picked up).</para>
/// </summary>
public static class ExecutableResolver
{
    private static readonly TimeSpan MissRetry = TimeSpan.FromSeconds(30);
    private static readonly ConcurrentDictionary<string, (string? Path, long AtTicks)> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The absolute path of <paramref name="name"/> found on PATH, or <c>null</c> when it isn't there.</summary>
    public static string? Find(string name)
    {
        if (Cache.TryGetValue(name, out var hit))
        {
            if (hit.Path is { } p ? File.Exists(p) : Environment.TickCount64 - hit.AtTicks < MissRetry.TotalMilliseconds)
                return hit.Path;
        }
        bool windows = OperatingSystem.IsWindows();
        var found = Find(name, Environment.GetEnvironmentVariable("PATH"),
            windows ? Environment.GetEnvironmentVariable("PATHEXT") : null, windows);
        Cache[name] = (found, Environment.TickCount64);
        return found;
    }

    /// <summary><see cref="Find(string)"/>, falling back to the bare <paramref name="name"/> when it isn't on PATH, for
    /// callers that already tolerate a failed start (it then fails exactly as before). The fallback stays safe because
    /// the tray runs from its own install directory (Program.Main), so a bare-name search can't land in a repo.</summary>
    public static string Resolve(string name) => Find(name) ?? name;

    /// <summary>An absolute path for a tool that ships in the Windows system directory (<c>cmd.exe</c>,
    /// <c>rundll32.exe</c>); the bare name elsewhere.</summary>
    public static string SystemTool(string exe) =>
        OperatingSystem.IsWindows() ? Path.Combine(Environment.SystemDirectory, exe) : exe;

    /// <summary>An absolute path for a tool that ships in the Windows directory (<c>explorer.exe</c>).</summary>
    public static string WindowsTool(string exe) =>
        OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), exe) : exe;

    /// <summary>The pure search, split out for tests. On Windows a name without an extension is tried with each
    /// <paramref name="pathExt"/> extension in order (never bare — npm drops an extensionless <em>sh</em> script next
    /// to every <c>.cmd</c> shim); a name that already carries one of those extensions is tried as-is.</summary>
    internal static string? Find(string name, string? pathVar, string? pathExt, bool windows)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        // An absolute name needs no search; a relative one with a directory part is cwd-relative, which is the
        // very thing this avoids.
        if (Path.IsPathFullyQualified(name)) return IsExecutableFile(name, windows) ? name : null;
        if (name.IndexOfAny(['/', '\\']) >= 0) return null;
        if (string.IsNullOrEmpty(pathVar)) return null;

        string[] candidates;
        if (windows)
        {
            var exts = (string.IsNullOrWhiteSpace(pathExt) ? ".COM;.EXE;.BAT;.CMD" : pathExt)
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var own = Path.GetExtension(name);
            candidates = own.Length > 0 && exts.Contains(own, StringComparer.OrdinalIgnoreCase)
                ? [name]
                : exts.Select(e => name + e.ToLowerInvariant()).ToArray();   // PATHEXT is upper-case; paths read better lower
        }
        else
        {
            candidates = [name];
        }

        foreach (var raw in pathVar.Split(Path.PathSeparator))
        {
            var dir = raw.Trim().Trim('"');   // Windows PATH entries may be quoted
            if (dir.Length == 0 || !Path.IsPathFullyQualified(dir)) continue;
            foreach (var c in candidates)
            {
                try
                {
                    var full = Path.Combine(dir, c);
                    if (IsExecutableFile(full, windows)) return full;
                }
                catch { /* malformed entry — skip */ }
            }
        }
        return null;
    }

    private static bool IsExecutableFile(string path, bool windows)
    {
        if (!File.Exists(path)) return false;
        if (windows || OperatingSystem.IsWindows()) return true;   // no exec bit to check (or a Windows test host)
        try
        {
            return (File.GetUnixFileMode(path)
                    & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        }
        catch { return false; }
    }
}

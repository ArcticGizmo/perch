namespace Perch.Data;

/// <summary>
/// Decides whether a path that came from content Perch didn't write (a transcript's inline code, a repo's
/// <c>.git</c> file) may be touched on disk at all. On Windows even a <c>File.Exists</c> on <c>\\host\share\x</c>
/// opens an SMB (or WebDAV) connection, which authenticates as the user — handing their NTLM hash to whoever
/// runs that host — and blocks the calling thread for the network timeout. So a network-shaped or device path
/// is never probed unless the user themselves is already working on that share (review fixes CP9,
/// docs/review-fixes-plan.md). Pure apart from a drive-type lookup; never throws.
/// </summary>
public static class LocalPath
{
    /// <summary>
    /// True when <paramref name="path"/> is safe to probe (<c>File.Exists</c>, read): a local path on a fixed or
    /// removable drive. A UNC path (<c>\\h\s</c>, <c>//h/s</c>), a device path (<c>\\?\</c>, <c>\\.\</c>), or a
    /// path on a network/optical/unmapped drive is refused — <em>unless</em> it has the same root as
    /// <paramref name="trustedRoot"/> (the session cwd, or the folder holding the <c>.git</c> file), i.e. the
    /// user already works on that share, so probing it reaches no host they haven't chosen. A relative path is
    /// safe: it resolves under <paramref name="trustedRoot"/>, and <c>..</c> can't climb off that volume.
    /// </summary>
    public static bool IsSafeToProbe(string? path, string? trustedRoot = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            if (!IsNetworkShaped(path) && !Path.IsPathRooted(path)) return true;
            if (SameRoot(path, trustedRoot)) return true;
            if (IsNetworkShaped(path)) return false;
            return !OperatingSystem.IsWindows() || IsLocalDrive(path);
        }
        catch { return false; }
    }

    /// <summary>UNC (<c>\\h\s</c>), device (<c>\\?\</c>, <c>\\.\</c>) and the forward-slash or mixed forms
    /// (<c>//h/s</c>, <c>\/h</c>) — every spelling Windows treats as "\\" at the start of a path.</summary>
    public static bool IsNetworkShaped(string s) =>
        s.Length >= 2 && s[0] is ('\\' or '/') && s[1] is ('\\' or '/');

    // A drive letter that's a local disk. A mapped network drive reaches SMB just like UNC; an optical or
    // unmapped one can stall. GetDriveType itself does no network IO.
    private static bool IsLocalDrive(string path)
    {
        var root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root) || root.Length < 2 || root[1] != ':') return false;
        return new DriveInfo(root[..2]).DriveType is DriveType.Fixed or DriveType.Removable or DriveType.Ram;
    }

    // Both rooted and on the same volume (drive or \\server\share), case-insensitively. The trusted root is only
    // trusted as far as it's rooted itself; relative roots never vouch for anything.
    private static bool SameRoot(string path, string? trustedRoot)
    {
        if (string.IsNullOrEmpty(trustedRoot) || !Path.IsPathRooted(trustedRoot)) return false;
        var a = Volume(path);
        var b = Volume(trustedRoot);
        return a.Length > 0 && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    // The volume part of a rooted path, slash-normalised: "C:" or "\\server\share". A device path yields "" — it
    // never matches, since "\\?\UNC\h\s" and "\\h\s" would otherwise need to be reconciled.
    private static string Volume(string path)
    {
        var p = path.Replace('/', '\\');
        if (p.StartsWith(@"\\?\", StringComparison.Ordinal) || p.StartsWith(@"\\.\", StringComparison.Ordinal)) return "";
        if (p.StartsWith(@"\\", StringComparison.Ordinal))
        {
            var parts = p[2..].Split('\\', 3);
            return parts.Length >= 2 && parts[0].Length > 0 && parts[1].Length > 0 ? $@"\\{parts[0]}\{parts[1]}" : "";
        }
        if (p.Length >= 2 && p[1] == ':' && char.IsAsciiLetter(p[0])) return p[..2];
        return p.StartsWith('\\') ? @"\" : "";   // POSIX "/" (or a Windows rooted-no-drive "\x")
    }
}

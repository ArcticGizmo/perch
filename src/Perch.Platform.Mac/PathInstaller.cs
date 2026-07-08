using Perch.Platform;

namespace Perch.Platform.Mac;

/// <summary>
/// macOS <see cref="IPathInstaller"/>: symlinks the running executable into <c>~/.local/bin/perch</c> so
/// the <c>perch</c> command resolves in any shell without sudo. (System-wide <c>/usr/local/bin</c> needs
/// elevation, which the app process doesn't have; a Phase-5 privileged installer step can move it there
/// if we decide the CLI must be globally visible — the plugin's <c>open -a</c> launch and
/// <c>pgrep -x perch</c> checks don't require PATH at all.)
///
/// Pure managed file ops — no native interop — so the behaviour is the same everywhere; it just points at
/// the macOS home dir. Run from the installer hooks (wired in Phase 5). Best-effort; never throws.
/// </summary>
public sealed class PathInstaller : IPathInstaller
{
    private static string LinkPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "perch");

    public void Register()
    {
        try
        {
            string? target = Environment.ProcessPath;
            if (string.IsNullOrEmpty(target)) return;

            string link = LinkPath;
            Directory.CreateDirectory(Path.GetDirectoryName(link)!);
            File.Delete(link); // remove any stale link/file first (no-op if absent); CreateSymbolicLink won't overwrite
            File.CreateSymbolicLink(link, target);
        }
        catch { /* best-effort: a read-only home or existing non-link just means no `perch` on PATH */ }
    }

    public void Unregister()
    {
        try { File.Delete(LinkPath); } // deletes the symlink itself, not its target; no-op if absent
        catch { /* best-effort */ }
    }
}

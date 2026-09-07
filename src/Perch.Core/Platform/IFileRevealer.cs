namespace Perch.Platform;

/// <summary>
/// Reveals a file in the OS file manager and opens it in the user's code editor — the two "take me to this
/// file" affordances the session UI's file references offer beyond a plain shell open. Both are genuinely
/// OS-specific (Windows Explorer's <c>/select</c>, Finder's <c>open -R</c>, the <c>code</c> CLI), so they
/// sit behind this seam like every other platform capability. Best-effort: a missing target, no editor
/// installed, or a blocked launch just no-ops — never throws.
/// </summary>
public interface IFileRevealer
{
    /// <summary>Opens the OS file manager with <paramref name="path"/> selected (a directory is opened
    /// directly). No-op on a blank/unresolvable path.</summary>
    void RevealInFileManager(string path);

    /// <summary>Opens <paramref name="path"/> in VS Code (the <c>code</c> CLI), jumping to
    /// <paramref name="line"/> when &gt; 0. Falls back to a shell open of the file when no editor is found.</summary>
    void OpenInEditor(string path, int line = 0);

    /// <summary>Shows the OS "Open with…" application chooser for <paramref name="path"/> (Windows'
    /// <c>OpenAs_RunDLL</c>, macOS falls back to the default handler). No-op on a blank/unresolvable path.</summary>
    void OpenWith(string path);
}

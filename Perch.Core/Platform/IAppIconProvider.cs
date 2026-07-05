namespace Perch.Platform;

/// <summary>
/// The platform's application-shell seam for the overlay's quick-links strip: resolving an installed
/// app's icon and launching it by its shell identity. Both are inherently OS-specific — on Windows the
/// icon comes from the shell image factory (the only way to get a Store/MSIX app's real logo) and the
/// launch goes through the AppsFolder; other platforms use their own icon theme / launch services.
///
/// Icons cross the boundary as a <em>PNG file on disk</em>, not an in-memory bitmap: the implementation
/// materialises the icon into a cache directory and returns the path. That keeps the contract neutral
/// (any UI toolkit just loads a PNG file), keeps every platform bitmap type behind the seam, and gives a
/// persistent cache for free — the stable shape for the eventual macOS/Linux implementations.
/// </summary>
public interface IAppIconProvider
{
    /// <summary>
    /// Returns the path to a cached PNG of the app's icon at (about) <paramref name="pixelSize"/>, or
    /// null when no icon resolves (the strip then draws name-derived initials). The three inputs mirror
    /// a quick link's identity: its display <paramref name="name"/> (used for a Start-Menu/Store lookup),
    /// an <paramref name="explicitPath"/> if the user pinned one, and the <paramref name="resolvedPath"/>
    /// discovered for a well-known app. Best-effort; never throws.
    /// </summary>
    string? GetIconFile(string name, string? explicitPath, string? resolvedPath, int pixelSize);

    /// <summary>Best-effort launch of an app by its shell display <paramref name="name"/> — for
    /// Store/MSIX apps that have no plain executable to start. Returns true if a launch was issued.
    /// Never throws.</summary>
    bool TryLaunchByName(string name);
}

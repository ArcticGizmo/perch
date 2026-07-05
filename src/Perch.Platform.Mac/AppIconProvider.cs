using Perch.Platform;

namespace Perch.Platform.Mac;

/// <summary>
/// macOS <see cref="IAppIconProvider"/>. Phase 3: resolve an app's icon via
/// <c>NSWorkspace.iconForFile</c>/<c>iconForContentType</c>, materialise it to the PNG cache (the same
/// on-disk contract), and launch via <c>NSWorkspace</c> / <c>open -a</c>. Stub for now: returns "no
/// icon" (the strip falls back to name-derived initials) and reports no launch.
/// </summary>
public sealed class AppIconProvider : IAppIconProvider
{
    public string? GetIconFile(string name, string? explicitPath, string? resolvedPath, int pixelSize) => null;
    public bool TryLaunchByName(string name) => false;
}

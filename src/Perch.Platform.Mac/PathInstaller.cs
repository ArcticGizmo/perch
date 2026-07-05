using Perch.Platform;

namespace Perch.Platform.Mac;

/// <summary>
/// macOS <see cref="IPathInstaller"/>. Phase 3: symlink the launcher into <c>/usr/local/bin</c>
/// (or <c>~/.local/bin</c>) so <c>perch</c> resolves in any shell, and remove it on uninstall.
/// Stub for now: no-op.
/// </summary>
public sealed class PathInstaller : IPathInstaller
{
    public void Register() { }
    public void Unregister() { }
}

namespace Perch.Platform;

/// <summary>
/// Adds/removes the install directory to the user's shell search path so the <c>perch</c> command (and
/// the plugin) can be invoked from any terminal. Windows uses the per-user PATH env var; other
/// platforms will symlink into a bin dir or edit a shell profile. Run from the installer hooks.
/// </summary>
public interface IPathInstaller
{
    void Register();
    void Unregister();
}

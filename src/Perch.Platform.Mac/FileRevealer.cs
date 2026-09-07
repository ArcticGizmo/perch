using System.Diagnostics;
using Perch.Platform;

namespace Perch.Platform.Mac;

/// <summary>
/// macOS <see cref="IFileRevealer"/>. <see cref="RevealInFileManager"/> reveals the file in Finder
/// (<c>open -R</c>); <see cref="OpenInEditor"/> launches VS Code via the <c>code</c> CLI, falling back to
/// <c>open</c> (the default handler) when VS Code isn't installed. Best-effort; never throws.
/// </summary>
public sealed class FileRevealer : IFileRevealer
{
    public void RevealInFileManager(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            var psi = new ProcessStartInfo("open") { UseShellExecute = false };
            psi.ArgumentList.Add("-R");
            psi.ArgumentList.Add(path);
            Process.Start(psi);
        }
        catch { /* best-effort */ }
    }

    public void OpenInEditor(string path, int line = 0)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            var psi = new ProcessStartInfo("code") { UseShellExecute = false };
            if (line > 0) { psi.ArgumentList.Add("-g"); psi.ArgumentList.Add($"{path}:{line}"); }
            else psi.ArgumentList.Add(path);
            Process.Start(psi);
        }
        catch
        {
            try
            {
                var psi = new ProcessStartInfo("open") { UseShellExecute = false };
                psi.ArgumentList.Add(path);
                Process.Start(psi);
            }
            catch { /* give up quietly */ }
        }
    }

    public void OpenWith(string path)
    {
        // macOS has no simple CLI "Open with…" chooser; open with the default handler as the closest action.
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            var psi = new ProcessStartInfo("open") { UseShellExecute = false };
            psi.ArgumentList.Add(path);
            Process.Start(psi);
        }
        catch { /* best-effort */ }
    }
}

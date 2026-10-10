using System.Diagnostics;
using Perch.Platform;

namespace Perch.Platform.Mac;

/// <summary>
/// macOS <see cref="IFileRevealer"/>. <see cref="RevealInFileManager"/> reveals the file in Finder
/// (<c>open -R</c>; a directory is opened directly); <see cref="OpenInEditor"/> launches VS Code via the <c>code</c> CLI, falling back to
/// <c>open</c> (the default handler) when VS Code isn't installed. Best-effort; never throws.
/// </summary>
public sealed class FileRevealer : IFileRevealer
{
    // Absolute: .NET on Unix checks the current directory before PATH for a bare name (review fixes CP7).
    private const string Open = "/usr/bin/open";

    public void RevealInFileManager(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            var psi = new ProcessStartInfo(Open) { UseShellExecute = false };
            // A directory opens directly (`open <dir>` shows it in Finder); `-R` would select it in its parent.
            if (!Directory.Exists(path)) psi.ArgumentList.Add("-R");
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
            var psi = new ProcessStartInfo(Perch.Data.ExecutableResolver.Resolve("code")) { UseShellExecute = false };
            if (line > 0) { psi.ArgumentList.Add("-g"); psi.ArgumentList.Add($"{path}:{line}"); }
            else psi.ArgumentList.Add(path);
            Process.Start(psi);
        }
        catch
        {
            // Never `open` a type that would run (a .command/.app file ref; review fixes CP8/CP12).
            OpenWithDefault(path);
        }
    }

    public void OpenWithDefault(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        if (!Perch.Data.OpenTargets.IsViewerSafeFile(path)) { RevealInFileManager(path); return; }
        try
        {
            var psi = new ProcessStartInfo(Open) { UseShellExecute = false };
            psi.ArgumentList.Add(path);
            Process.Start(psi);
        }
        catch { /* best-effort */ }
    }

    public void OpenWith(string path)
    {
        // macOS has no simple CLI "Open with…" chooser; the (viewer-gated) default handler is the closest action.
        OpenWithDefault(path);
    }
}

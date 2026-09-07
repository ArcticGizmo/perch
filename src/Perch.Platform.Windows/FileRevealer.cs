using System.Diagnostics;
using Perch.Platform;

namespace Perch.Platform.Windows;

/// <summary>
/// Windows <see cref="IFileRevealer"/>. <see cref="RevealInFileManager"/> launches Explorer with the file
/// selected (<c>explorer.exe /select,"path"</c> — Explorer parses the <c>/select,&lt;path&gt;</c> token as a
/// single command-line string, so it is passed as a raw argument string, not an argument list). A directory is
/// opened directly. <see cref="OpenInEditor"/> launches VS Code via the <c>code</c> launcher on PATH
/// (<c>code -g path:line</c>), falling back to the file's default handler when VS Code isn't installed.
/// </summary>
public sealed class FileRevealer : IFileRevealer
{
    public void RevealInFileManager(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            if (File.Exists(path))
                // Explorer wants "/select,<path>" as one command-line string; ArgumentList would split it.
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false });
            else if (Directory.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = false });
        }
        catch { /* best-effort */ }
    }

    public void OpenInEditor(string path, int line = 0)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            // `code` is a .cmd shim on PATH; UseShellExecute lets the shell resolve it (via PATH + PATHEXT).
            var psi = new ProcessStartInfo("code") { UseShellExecute = true };
            if (line > 0) { psi.ArgumentList.Add("-g"); psi.ArgumentList.Add($"{path}:{line}"); }
            else psi.ArgumentList.Add(path);
            Process.Start(psi);
        }
        catch
        {
            // No VS Code (or launch blocked): open the file with its default handler so the action isn't a dead end.
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
            catch { /* give up quietly */ }
        }
    }

    public void OpenWith(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        try
        {
            // The shell's "Open with…" chooser dialog.
            var psi = new ProcessStartInfo("rundll32.exe") { UseShellExecute = false };
            psi.ArgumentList.Add("shell32.dll,OpenAs_RunDLL");
            psi.ArgumentList.Add(path);
            Process.Start(psi);
        }
        catch { /* best-effort */ }
    }
}

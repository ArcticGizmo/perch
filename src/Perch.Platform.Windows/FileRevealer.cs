using System.Diagnostics;
using Perch.Data;
using Perch.Platform;

namespace Perch.Platform.Windows;

/// <summary>
/// Windows <see cref="IFileRevealer"/>. <see cref="RevealInFileManager"/> launches Explorer with the file
/// selected (<c>explorer.exe /select,"path"</c> — Explorer parses the <c>/select,&lt;path&gt;</c> token as a
/// single command-line string, so it is passed as a raw argument string, not an argument list). A directory is
/// opened directly. <see cref="OpenInEditor"/> launches VS Code via the <c>code</c> launcher on PATH
/// (<c>code -g path:line</c>), falling back to the file's default handler when VS Code isn't installed. The launcher is
/// a <c>code.cmd</c> shim, so it goes through <see cref="CmdShim"/>, which starts <c>Code.exe</c> itself: cmd never
/// parses the path (review fixes CP12).
/// </summary>
public sealed class FileRevealer : IFileRevealer
{
    // By absolute path, never a bare name a working directory could shadow (review fixes CP7).
    private static string Explorer => ExecutableResolver.WindowsTool("explorer.exe");

    public void RevealInFileManager(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            if (File.Exists(path))
                // Explorer wants "/select,<path>" as one command-line string; ArgumentList would split it.
                Process.Start(new ProcessStartInfo(Explorer, $"/select,\"{path}\"") { UseShellExecute = false });
            else if (Directory.Exists(path))
                Process.Start(new ProcessStartInfo(Explorer, $"\"{path}\"") { UseShellExecute = false });
        }
        catch { /* best-effort */ }
    }

    public void OpenInEditor(string path, int line = 0)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            // `code` is a .cmd shim on PATH, resolved to an absolute path rather than left to ShellExecute, which
            // would look in the current directory first (review fixes CP7). Shell-executing the shim meant cmd parsed
            // the path, so a file named `x&calc&.md` ran calc; CmdShim starts Code.exe directly instead, or refuses
            // (review fixes CP12). Not found or refused → the fallback below.
            var code = ExecutableResolver.Find("code") ?? throw new FileNotFoundException("VS Code isn't on PATH.");
            string[] args = line > 0 ? ["-g", $"{path}:{line}"] : [path];
            var psi = CmdShim.StartInfo(code, args) ?? throw new InvalidOperationException("Unsafe for the code shim.");
            Process.Start(psi)?.Dispose();
        }
        catch
        {
            // No VS Code (or launch blocked): the default handler, so the action isn't a dead end — but never a
            // shell open of a type that would run (a `.bat` file ref; review fixes CP8/CP12).
            OpenWithDefault(path);
        }
    }

    public void OpenWithDefault(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        if (!OpenTargets.IsViewerSafeFile(path)) { RevealInFileManager(path); return; }
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch { /* best-effort — no handler, blocked, etc. */ }
    }

    public void OpenWith(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        try
        {
            // The shell's "Open with…" chooser dialog.
            var psi = new ProcessStartInfo(ExecutableResolver.SystemTool("rundll32.exe")) { UseShellExecute = false };
            psi.ArgumentList.Add("shell32.dll,OpenAs_RunDLL");
            psi.ArgumentList.Add(path);
            Process.Start(psi);
        }
        catch { /* best-effort */ }
    }
}

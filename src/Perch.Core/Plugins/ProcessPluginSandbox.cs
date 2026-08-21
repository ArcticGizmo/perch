namespace Perch.Plugins;

using System.Diagnostics;
using System.Text;

/// <summary>
/// The default plugin launcher: a plain child process with stdio redirected, no inherited handles, and its
/// working directory pinned to the plugin folder. It provides the process-boundary isolation of M1 — a
/// crash or hang can't touch the tray — but <em>not</em> a permission sandbox. The hardened,
/// restricted-token launch is a later platform-specific <see cref="IPluginSandbox"/> (see M4 in
/// docs/pluggability-plan.md); this cross-platform version is the honest floor.
/// </summary>
internal sealed class ProcessPluginSandbox : IPluginSandbox
{
    public IPluginProcess Launch(PluginLaunchSpec spec)
    {
        var proc = Process.Start(BuildStartInfo(spec))
            ?? throw new InvalidOperationException($"failed to start plugin process '{spec.Command}'.");
        return new ProcessHandle(proc);
    }

    /// <summary>The shared <see cref="ProcessStartInfo"/> every sandbox uses: stdio redirected, no window,
    /// working dir pinned to the plugin folder, and UTF-8 fixed in both directions so a plugin's unicode
    /// glyphs survive the pipe regardless of the child's console codepage. Exposed so the hardened Windows
    /// sandbox reuses the exact same launch shape (and just adds a Job Object / AppContainer around it).</summary>
    internal static ProcessStartInfo BuildStartInfo(PluginLaunchSpec spec)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ResolveCommand(spec),
            WorkingDirectory = spec.WorkingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false),
        };
        foreach (var arg in spec.Args) psi.ArgumentList.Add(arg);
        return psi;
    }

    // A command that is a bare launcher name (powershell, node, python) is left for PATH resolution; a
    // relative path is resolved against the plugin folder so it can't reference a program elsewhere.
    private static string ResolveCommand(PluginLaunchSpec spec)
    {
        var cmd = spec.Command;
        if (cmd.Contains('/') || cmd.Contains('\\'))
        {
            var full = Path.GetFullPath(Path.Combine(spec.WorkingDirectory, cmd));
            if (File.Exists(full)) return full;
        }
        return cmd;
    }

    private sealed class ProcessHandle(Process proc) : IPluginProcess
    {
        public TextWriter StandardInput => proc.StandardInput;
        public TextReader StandardOutput => proc.StandardOutput;

        public async Task<int> WaitForExitAsync(CancellationToken ct)
        {
            await proc.WaitForExitAsync(ct);
            return proc.ExitCode;
        }

        public void Kill()
        {
            try { proc.Kill(entireProcessTree: true); }
            catch { /* already gone */ }
        }

        public void Dispose()
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            proc.Dispose();
        }
    }
}

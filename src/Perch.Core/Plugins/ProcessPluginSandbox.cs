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
        var psi = new ProcessStartInfo
        {
            FileName = ResolveCommand(spec),
            WorkingDirectory = spec.WorkingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            // Fix the protocol encoding to UTF-8 in both directions so a plugin's unicode glyphs (emoji,
            // symbols) survive the pipe regardless of the child's console codepage — the alternative is the
            // Windows OEM codepage mangling anything outside ASCII. Plugins are documented to emit UTF-8.
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false),
        };
        foreach (var arg in spec.Args) psi.ArgumentList.Add(arg);

        var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"failed to start plugin process '{spec.Command}'.");
        return new ProcessHandle(proc);
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

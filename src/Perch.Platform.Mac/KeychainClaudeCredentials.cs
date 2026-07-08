using System.Diagnostics;
using Perch.Platform;

namespace Perch.Platform.Mac;

/// <summary>
/// macOS <see cref="IClaudeCredentials"/>: Claude Code stores its OAuth blob in the login <b>Keychain</b>
/// (a generic-password item with service <c>Claude Code-credentials</c>), not in
/// <c>~/.claude/.credentials.json</c> as on Windows/Linux — so the file-only reader wrongly reported
/// "sign in to Claude Code" on a Mac. This reads the item's secret with the stock <c>/usr/bin/security</c>
/// CLI (the same shell-based approach the mac <see cref="AppIconProvider"/> uses), and the secret is the
/// exact same JSON the file would hold, so the usage monitor parses it unchanged.
///
/// Falls back to <see cref="FileClaudeCredentials"/> if the Keychain read yields nothing — covers a Linux
/// head on this same non-Windows build, and a user who has a credentials file for any reason. Best-effort;
/// never throws. Note: the first read may raise a one-time Keychain access prompt (the item was created by
/// Claude Code); once the user allows it, subsequent reads are silent.
/// </summary>
public sealed class KeychainClaudeCredentials : IClaudeCredentials
{
    private const string ServiceName = "Claude Code-credentials";

    private readonly FileClaudeCredentials _file = new();

    public string? ReadCredentialsJson()
    {
        var json = ReadFromKeychain();
        return string.IsNullOrWhiteSpace(json) ? _file.ReadCredentialsJson() : json;
    }

    // `security find-generic-password -s "<service>" -w` prints just the secret (the JSON blob) to stdout,
    // or exits non-zero if the item is absent / access is denied. Queried by service alone (it's unique)
    // so a mismatched account name can't hide it.
    private static string? ReadFromKeychain()
    {
        try
        {
            var psi = new ProcessStartInfo("/usr/bin/security")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("find-generic-password");
            psi.ArgumentList.Add("-s");
            psi.ArgumentList.Add(ServiceName);
            psi.ArgumentList.Add("-w");

            using var p = Process.Start(psi);
            if (p is null) return null;
            string outp = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(10_000)) { try { p.Kill(true); } catch { } return null; }
            return p.ExitCode == 0 && !string.IsNullOrWhiteSpace(outp) ? outp.Trim() : null;
        }
        catch
        {
            return null;
        }
    }
}

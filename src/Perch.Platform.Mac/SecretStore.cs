using System.Diagnostics;
using Perch.Platform;

namespace Perch.Platform.Mac;

/// <summary>
/// macOS <see cref="ISecretStore"/>: keeps each secret as a generic-password item in the login <b>Keychain</b>
/// (service <c>Perch</c>, account = the key), read/written with the stock <c>/usr/bin/security</c> CLI — the
/// same shell-based approach <see cref="KeychainClaudeCredentials"/> uses. So Perch's OAuth refresh token is
/// protected by the Keychain exactly like any other app credential, never sitting in a plaintext file.
///
/// Best-effort throughout; never throws. Note: as with the credentials read, the first access to an item may
/// raise a one-time Keychain prompt; once allowed, subsequent reads are silent.
/// </summary>
public sealed class KeychainSecretStore : ISecretStore
{
    private const string ServiceName = "Perch";

    public void Set(string key, string value)
    {
        // -U updates in place if the item already exists; otherwise it's created.
        Security("add-generic-password", "-U", "-s", ServiceName, "-a", key, "-w", value);
    }

    public string? Get(string key)
    {
        var (code, output) = SecurityCapture("find-generic-password", "-s", ServiceName, "-a", key, "-w");
        return code == 0 && !string.IsNullOrWhiteSpace(output) ? output.Trim() : null;
    }

    public void Delete(string key)
    {
        Security("delete-generic-password", "-s", ServiceName, "-a", key);
    }

    private static void Security(params string[] args)
    {
        try { SecurityCapture(args); } catch { /* best-effort */ }
    }

    private static (int code, string output) SecurityCapture(params string[] args)
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
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var p = Process.Start(psi);
            if (p is null) return (-1, "");
            string outp = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(10_000)) { try { p.Kill(true); } catch { } return (-1, ""); }
            return (p.ExitCode, outp);
        }
        catch
        {
            return (-1, "");
        }
    }
}

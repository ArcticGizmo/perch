using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Perch.Remote;

/// <summary>What one Perch is to another it paired with (docs/remote-workers-plan.md).</summary>
internal enum PairedRole
{
    /// <summary>It may drive this machine's sessions (this machine is its worker).</summary>
    Controller,

    /// <summary>This machine drives its sessions (it is this machine's worker).</summary>
    Worker,
}

/// <summary>A machine paired once, recognised afterwards by its pinned key. Persisted on <c>AppSettings.PairedDevices</c>.</summary>
internal sealed class PairedDevice
{
    public string Name { get; set; } = "";

    /// <summary><see cref="DeviceTrust.PinOf"/> of its certificate: the only thing that identifies it.</summary>
    public string Pin { get; set; } = "";

    public PairedRole Role { get; set; }

    /// <summary>Where a worker is reached (a host name, IP or Tailscale name). Unused for a controller.</summary>
    public string? Host { get; set; }
    public int Port { get; set; }

    public DateTime AddedUtc { get; set; }
}

internal static class DeviceTrust
{
    /// <summary>The port a worker listens on unless set otherwise; next to the OAuth loopback ports (41532-41535).</summary>
    public const int DefaultPort = 41540;

    /// <summary>A certificate's identity: SHA-256 of its public key (SubjectPublicKeyInfo), as upper-case hex. The key is
    /// long-lived and the certificate around it is minted on each start, so the key is what's pinned.</summary>
    public static string PinOf(X509Certificate certificate)
    {
        using var c = new X509Certificate2(certificate);
        return Convert.ToHexString(SHA256.HashData(c.PublicKey.ExportSubjectPublicKeyInfo()));
    }

    /// <summary>The six digits both machines show while pairing ("482 913"). Made from both pins in a fixed order, so
    /// each side gets the same code; a machine in the middle would present its own key on each side, and the codes
    /// wouldn't match.</summary>
    public static string PairingCode(string pinA, string pinB)
    {
        var (first, second) = string.CompareOrdinal(pinA, pinB) <= 0 ? (pinA, pinB) : (pinB, pinA);
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(first + "|" + second));
        var n = BitConverter.ToUInt32(hash, 0) % 1_000_000;
        var digits = n.ToString("D6");
        return digits[..3] + " " + digits[3..];
    }

    /// <summary>A short, readable form of a pin for Settings ("3F2A·9C01·…").</summary>
    public static string Fingerprint(string pin) =>
        pin.Length < 16 ? pin : $"{pin[..4]}·{pin[4..8]}·{pin[8..12]}·{pin[12..16]}";

    /// <summary>This machine's name as peers see it: the user's choice, else the machine name.</summary>
    public static string DeviceName(string? chosen) =>
        string.IsNullOrWhiteSpace(chosen) ? Environment.MachineName : chosen.Trim();
}

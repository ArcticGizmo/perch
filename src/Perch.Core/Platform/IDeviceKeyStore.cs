using System.Security.Cryptography.X509Certificates;

namespace Perch.Platform;

/// <summary>
/// This Perch's identity to the machines it pairs with for remote workers (docs/remote-workers-plan.md): a long-lived
/// ECDSA P-256 key kept in the OS key store, never exported. Peers pin its public key, so the certificate around it is
/// minted fresh on each load and never stored. A platform seam because a key TLS can use from the OS store is
/// OS-specific: Windows can't hand SChannel a key imported from bytes as ephemeral (W0), so it's a named CNG key there.
/// </summary>
public interface IDeviceKeyStore
{
    /// <summary>Whether this head can hold a device key at all. False hides remote workers.</summary>
    bool IsSupported { get; }

    /// <summary>A self-signed certificate over the device key, with the private key attached and usable by
    /// <see cref="System.Net.Security.SslStream"/>; the key is made on first use. Null when unsupported or the key
    /// store failed. The caller owns (disposes) the certificate; the key itself persists.</summary>
    X509Certificate2? LoadOrCreate();

    /// <summary>Deletes the device key, so the next load makes a new identity and every pairing has to be redone.
    /// Best-effort.</summary>
    void Reset();
}

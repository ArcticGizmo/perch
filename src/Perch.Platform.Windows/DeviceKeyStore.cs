using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Perch.Platform;

namespace Perch.Platform.Windows;

/// <summary>
/// The device key as a named, persisted CNG key in the current user's key store, created non-exportable. A key
/// imported from PFX bytes can't be ephemeral for SChannel, and a non-ephemeral import leaves a key file behind on
/// every load (docs/remote-workers-plan.md, W0), so the key lives under a fixed name instead.
/// </summary>
public sealed class DeviceKeyStore(string keyName) : IDeviceKeyStore
{
    public bool IsSupported => true;

    public X509Certificate2? LoadOrCreate()
    {
        try
        {
            using var key = CngKey.Exists(keyName)
                ? CngKey.Open(keyName)
                : CngKey.Create(CngAlgorithm.ECDsaP256, keyName, new CngKeyCreationParameters
                {
                    ExportPolicy = CngExportPolicies.None,
                    Provider = CngProvider.MicrosoftSoftwareKeyStorageProvider,
                });
            using var ecdsa = new ECDsaCng(key);
            var request = new CertificateRequest("CN=Perch", ecdsa, HashAlgorithmName.SHA256);
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                [new Oid("1.3.6.1.5.5.7.3.1"), new Oid("1.3.6.1.5.5.7.3.2")], critical: false));   // server + client auth
            return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(20));
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    public void Reset()
    {
        try
        {
            if (!CngKey.Exists(keyName)) return;
            using var key = CngKey.Open(keyName);
            key.Delete();
        }
        catch (CryptographicException) { /* best-effort */ }
    }
}

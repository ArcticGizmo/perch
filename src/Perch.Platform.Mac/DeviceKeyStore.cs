using System.Security.Cryptography.X509Certificates;
using Perch.Platform;

namespace Perch.Platform.Mac;

/// <summary>
/// Not built yet: the device key belongs in the login Keychain, and SslStream's use of a self-signed identity from it
/// is unverified (docs/remote-workers-plan.md, W6). Until then remote workers aren't offered on macOS.
/// </summary>
public sealed class DeviceKeyStore : IDeviceKeyStore
{
    public bool IsSupported => false;
    public X509Certificate2? LoadOrCreate() => null;
    public void Reset() { }
}

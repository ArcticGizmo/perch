namespace Perch.Platform;

/// <summary>
/// A tiny per-user secret store: string values kept under string keys, encrypted at rest by the platform's
/// own facility (Windows DPAPI, macOS Keychain). Perch uses it to hold the Social feature's OAuth refresh
/// token — never in plaintext settings — so a signed-in user stays signed in across restarts without the
/// token ever landing in <c>settings.json</c>.
///
/// This is a platform seam because <em>where and how</em> a secret is protected differs per OS. The
/// portable <see cref="FileSecretStore"/> supplies the file-and-dictionary mechanics; each head plugs in
/// its own <see cref="ISecretProtector"/> (or, on macOS, its own Keychain-backed store). Best-effort: a
/// failed read returns null and a failed write is swallowed, so a missing keystore degrades to "not signed
/// in" rather than throwing.
/// </summary>
public interface ISecretStore
{
    /// <summary>Stores (or replaces) the secret under <paramref name="key"/>. Best-effort; never throws.</summary>
    void Set(string key, string value);

    /// <summary>The secret stored under <paramref name="key"/>, or null when absent/unreadable.</summary>
    string? Get(string key);

    /// <summary>Removes the secret under <paramref name="key"/> if present. Best-effort; never throws.</summary>
    void Delete(string key);
}

/// <summary>
/// Encrypts/decrypts a secret's bytes at rest — the per-OS half of <see cref="FileSecretStore"/>. Windows
/// binds DPAPI (current-user scope); a head without an OS keystore uses <see cref="IdentitySecretProtector"/>
/// (no encryption — a last-resort fallback, e.g. a Linux head). Implementations must round-trip:
/// <c>Unprotect(Protect(x)) == x</c>.
/// </summary>
public interface ISecretProtector
{
    byte[] Protect(byte[] plaintext);
    byte[] Unprotect(byte[] ciphertext);
}

/// <summary>
/// The no-op protector: bytes are stored as-is. Used only where the OS offers no keystore (so the value is
/// merely base64 in a user-profile file, no better than the settings it's kept out of). Windows and macOS
/// never use this — they protect with DPAPI / the Keychain.
/// </summary>
public sealed class IdentitySecretProtector : ISecretProtector
{
    public byte[] Protect(byte[] plaintext) => plaintext;
    public byte[] Unprotect(byte[] ciphertext) => ciphertext;
}

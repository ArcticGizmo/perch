using System.Security.Cryptography;
using System.Text;

namespace Perch.Social;

/// <summary>A PKCE verifier + its S256 challenge, for the OAuth authorization-code-with-PKCE flow the
/// desktop sign-in uses (no client secret in the app).</summary>
public sealed record PkcePair(string Verifier, string Challenge);

/// <summary>
/// PKCE (RFC 7636) helpers. The verifier is 32 random bytes base64url-encoded (43 chars, within the 43–128
/// range the spec allows); the challenge is <c>base64url(SHA256(verifier))</c>. Base64url = standard base64
/// with <c>+/</c> mapped to <c>-_</c> and padding stripped.
/// </summary>
public static class Pkce
{
    public static PkcePair Create()
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return new PkcePair(verifier, challenge);
    }

    public static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

namespace Perch.Plugins;

using System.Security.Cryptography;
using System.Text.RegularExpressions;

/// <summary>
/// A parsed <c>SHA256SUMS.txt</c> (the sha256sum format: <c>&lt;64 hex&gt;  &lt;filename&gt;</c>, an
/// optional <c>*</c> before the name). Mirrors the verification install.ps1 already does for Perch itself:
/// a file that lists other names but not the one we're installing is a build error, so lookup fails
/// loudly rather than treating "no entry" as "nothing to check".
/// </summary>
internal sealed partial class Sha256Sums
{
    [GeneratedRegex(@"^\s*([0-9a-fA-F]{64})\s+\*?(.+?)\s*$")]
    private static partial Regex Line();

    private readonly Dictionary<string, string> _byName = new(StringComparer.Ordinal);

    public static Sha256Sums Parse(string text)
    {
        var sums = new Sha256Sums();
        foreach (var raw in text.Split('\n'))
        {
            var m = Line().Match(raw.TrimEnd('\r'));
            if (m.Success) sums._byName[m.Groups[2].Value] = m.Groups[1].Value.ToLowerInvariant();
        }
        return sums;
    }

    /// <summary>The expected lower-case hex hash for <paramref name="name"/>, or null if absent.</summary>
    public string? Expected(string name) => _byName.TryGetValue(name, out var h) ? h : null;

    /// <summary>Lower-case hex SHA-256 of some bytes, ready to compare against <see cref="Expected"/>.</summary>
    public static string Hash(byte[] bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));
}

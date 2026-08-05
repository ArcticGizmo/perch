namespace Perch.Tests;

/// <summary>Test helpers for text the committed source shouldn't spell out verbatim (profanity corpora for
/// the swear-filter tests). ROT13 is enough to keep it out of a casual grep while staying trivially
/// reversible in-test.</summary>
internal static class TestText
{
    public static string Rot13(string s)
    {
        var a = s.ToCharArray();
        for (int i = 0; i < a.Length; i++)
        {
            char c = a[i];
            if (c is >= 'a' and <= 'z') a[i] = (char)('a' + (c - 'a' + 13) % 26);
            else if (c is >= 'A' and <= 'Z') a[i] = (char)('A' + (c - 'A' + 13) % 26);
        }
        return new string(a);
    }
}

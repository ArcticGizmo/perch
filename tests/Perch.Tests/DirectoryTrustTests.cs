using System.Text.Json.Nodes;
using Perch.Data;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// Covers <see cref="DirectoryTrust"/>: the pure ancestor-walk decision + path canonicalisation, and the
/// best-effort read/write of the shared <c>.claude.json</c> trust store. The pure cases pass paths in; the IO
/// cases use a throwaway temp file, so nothing here touches the real store or depends on the host layout.
/// </summary>
public class DirectoryTrustTests
{
    // Absolute, host-appropriate paths so GetFullPath/DirectoryInfo behave on any OS the suite runs on.
    private static string Abs(params string[] parts) => Path.Combine(new[] { Path.GetTempPath() }.Concat(parts).ToArray());

    // ── Pure: ancestor-walk decision ─────────────────────────────────────────────

    [Fact]
    public void Trusts_the_exact_folder_and_its_subfolders()
    {
        var root = Abs("perch-trust", "proj");
        var deep = Path.Combine(root, "src", "app");

        Assert.True(DirectoryTrust.IsTrusted(new[] { root }, root));
        Assert.True(DirectoryTrust.IsTrusted(new[] { root }, deep));   // ancestor walk
    }

    [Fact]
    public void Does_not_trust_an_unrelated_or_parent_folder()
    {
        var root = Abs("perch-trust", "proj");
        var sibling = Abs("perch-trust", "other");
        var parent = Abs("perch-trust");

        Assert.False(DirectoryTrust.IsTrusted(new[] { root }, sibling));
        Assert.False(DirectoryTrust.IsTrusted(new[] { root }, parent));   // trusting a child never trusts its parent
        Assert.False(DirectoryTrust.IsTrusted(System.Array.Empty<string>(), root));
    }

    [Fact]
    public void Trusts_the_home_directory_implicitly_but_not_its_subfolders()
    {
        var home = Abs("perch-trust", "home");
        var sub = Path.Combine(home, "work");

        Assert.True(DirectoryTrust.IsTrusted(System.Array.Empty<string>(), home, home));
        Assert.False(DirectoryTrust.IsTrusted(System.Array.Empty<string>(), sub, home));
    }

    [Fact]
    public void Lookup_tolerates_separator_and_case_differences()
    {
        if (!OperatingSystem.IsWindows()) return;   // case-insensitive + slash tolerance are Windows/mac behaviour
        var root = @"C:\Users\Me\Proj";
        var keyOddSpelling = "c:/users/me/proj";

        Assert.True(DirectoryTrust.IsTrusted(new[] { keyOddSpelling }, root));
    }

    [Fact]
    public void CanonicalKey_is_absolute_forward_slash_without_trailing_separator()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Equal("C:/Users/Me/Proj", DirectoryTrust.CanonicalKey(@"C:\Users\Me\Proj\"));
    }

    [Fact]
    public void SameDir_matches_the_same_directory_and_rejects_others()
    {
        var a = Abs("perch-trust", "x");
        Assert.True(DirectoryTrust.SameDir(a, a + Path.DirectorySeparatorChar));
        Assert.False(DirectoryTrust.SameDir(a, Abs("perch-trust", "y")));
        Assert.False(DirectoryTrust.SameDir(null, a));
        Assert.False(DirectoryTrust.SameDir(a, null));
    }

    // ── IO: read/write the store ─────────────────────────────────────────────────

    [Fact]
    public void Grant_then_read_round_trips_and_marks_the_folder_trusted()
    {
        using var f = new TempFile();
        var cwd = Abs("perch-trust", "granted");

        Assert.True(DirectoryTrust.GrantAt(f.Path, cwd));
        Assert.True(File.Exists(f.Path));
        Assert.True(DirectoryTrust.IsTrusted(DirectoryTrust.ReadTrustedKeys(f.Path), cwd));
    }

    [Fact]
    public void Grant_preserves_other_fields_and_existing_projects()
    {
        using var f = new TempFile();
        File.WriteAllText(f.Path, """
            {
              "oauthAccount": { "emailAddress": "keep@example.com" },
              "projects": {
                "C:/existing/one": { "hasTrustDialogAccepted": true, "allowedTools": [] }
              }
            }
            """);

        Assert.True(DirectoryTrust.GrantAt(f.Path, Abs("perch-trust", "added")));

        var root = JsonNode.Parse(File.ReadAllText(f.Path))!.AsObject();
        Assert.Equal("keep@example.com", (string?)root["oauthAccount"]!["emailAddress"]);
        var projects = root["projects"]!.AsObject();
        Assert.True(projects.ContainsKey("C:/existing/one"));   // untouched
        Assert.Equal(2, projects.Count);                        // plus the new one
    }

    [Fact]
    public void Grant_reuses_an_existing_entry_for_the_same_directory()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var f = new TempFile();
        File.WriteAllText(f.Path, """
            { "projects": { "C:/Users/Me/Proj": { "hasTrustDialogAccepted": false, "allowedTools": ["Read"] } } }
            """);

        Assert.True(DirectoryTrust.GrantAt(f.Path, @"C:\Users\Me\Proj"));

        var projects = JsonNode.Parse(File.ReadAllText(f.Path))!["projects"]!.AsObject();
        Assert.Single(projects);   // no duplicate key for the same folder
        var entry = projects["C:/Users/Me/Proj"]!.AsObject();
        Assert.True((bool)entry["hasTrustDialogAccepted"]!);
        Assert.NotNull(entry["allowedTools"]);   // sibling fields on the entry survive
    }

    [Fact]
    public void Grant_refuses_to_clobber_a_non_object_file()
    {
        using var f = new TempFile();
        File.WriteAllText(f.Path, "not json at all");

        Assert.False(DirectoryTrust.GrantAt(f.Path, Abs("perch-trust", "x")));
        Assert.Equal("not json at all", File.ReadAllText(f.Path));
    }

    // ── CP14: .claude.json also holds the Claude sign-in; a grant must never replace it ──────────

    [Theory]
    [InlineData("")]          // 0 bytes: Claude mid-write, or damaged
    [InlineData("   \r\n")]
    [InlineData("[]")]        // valid JSON, but not an object
    [InlineData("null")]
    public void Grant_leaves_an_empty_or_non_object_file_byte_for_byte(string content)
    {
        using var f = new TempFile();
        File.WriteAllText(f.Path, content);

        Assert.False(DirectoryTrust.GrantAt(f.Path, Abs("perch-trust", "x")));
        Assert.Equal(content, File.ReadAllText(f.Path));
    }

    [Fact]
    public void Grant_against_a_locked_file_refuses_and_leaves_it_intact()
    {
        // The bug: a read failure was treated as "no file", so the grant wrote {"projects":{…}} over the user's
        // oauthAccount and every project. Now an unreadable file is refused.
        if (!OperatingSystem.IsWindows()) return;
        using var f = new TempFile();
        const string original = """{ "oauthAccount": { "emailAddress": "keep@example.com" }, "projects": {} }""";
        File.WriteAllText(f.Path, original);

        using (new FileStream(f.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.False(DirectoryTrust.GrantAt(f.Path, Abs("perch-trust", "x")));

        Assert.Equal(original, File.ReadAllText(f.Path));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(f.Path)!, Path.GetFileName(f.Path) + ".*.tmp"));
    }

    [Fact]
    public void Grant_for_an_already_trusted_folder_does_not_rewrite_the_file()
    {
        using var f = new TempFile();
        var cwd = Abs("perch-trust", "already");
        Assert.True(DirectoryTrust.GrantAt(f.Path, cwd));
        var old = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(f.Path, old);

        Assert.True(DirectoryTrust.GrantAt(f.Path, cwd));
        Assert.Equal(old, File.GetLastWriteTimeUtc(f.Path));
    }

    [Fact]
    public void ReadTrustedKeys_returns_only_accepted_entries_and_tolerates_a_missing_file()
    {
        using var f = new TempFile();
        File.WriteAllText(f.Path, """
            { "projects": {
                "C:/a": { "hasTrustDialogAccepted": true },
                "C:/b": { "hasTrustDialogAccepted": false },
                "C:/c": { }
            } }
            """);

        var keys = DirectoryTrust.ReadTrustedKeys(f.Path);
        Assert.Equal(new[] { "C:/a" }, keys);

        Assert.Empty(DirectoryTrust.ReadTrustedKeys(Path.Combine(Path.GetTempPath(), "perch-trust-nope.json")));
    }

    private sealed class TempFile : System.IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"perch-trust-{System.Guid.NewGuid():N}.json");

        public void Dispose()
        {
            try { if (File.Exists(Path)) File.Delete(Path); } catch { /* best-effort */ }
        }
    }
}

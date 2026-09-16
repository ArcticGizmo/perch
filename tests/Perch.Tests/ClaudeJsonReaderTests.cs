using Perch.Data;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// Covers the Layer-2 M0 live-org reader (<see cref="ClaudeJsonReader"/>) and the <see cref="Org"/>
/// value model: parsing a config dir's <c>.claude.json</c> <c>oauthAccount</c> block, and the
/// best-effort contract — a missing / malformed / signed-out / org-less file reads as <c>null</c>
/// rather than throwing. All fixtures are synthetic placeholders (no real account data).
/// </summary>
public class ClaudeJsonReaderTests : IDisposable
{
    private readonly string _dir;

    public ClaudeJsonReaderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "perch-orgtest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string WriteClaudeJson(string body)
    {
        var path = Path.Combine(_dir, ".claude.json");
        File.WriteAllText(path, body);
        return path;
    }

    [Fact]
    public void ReadsOrg_FromOAuthAccount()
    {
        var path = WriteClaudeJson("""
            {
              "oauthAccount": {
                "organizationUuid": "11111111-1111-1111-1111-111111111111",
                "organizationName": "Org A",
                "emailAddress": "someone@example.com"
              },
              "projects": {}
            }
            """);

        var org = ClaudeJsonReader.ReadLiveOrg(path);

        Assert.NotNull(org);
        Assert.Equal("11111111-1111-1111-1111-111111111111", org!.Uuid);
        Assert.Equal("Org A", org.Name);
        Assert.Equal("someone@example.com", org.AccountEmail);
        Assert.Equal("Org A", org.DisplayName);
    }

    [Fact]
    public void ReadsViaConfigDirAccessor()
    {
        WriteClaudeJson("""
            { "oauthAccount": { "organizationUuid": "abc", "organizationName": "Org B" } }
            """);

        var org = ClaudeJsonReader.ReadLiveOrg(new ClaudeConfigDir(_dir));

        Assert.Equal("abc", org!.Uuid);
        Assert.Equal("Org B", org.Name);
        Assert.Null(org.AccountEmail);
    }

    [Fact]
    public void MissingFile_ReadsNull()
    {
        Assert.Null(ClaudeJsonReader.ReadLiveOrg(Path.Combine(_dir, ".claude.json")));
    }

    [Fact]
    public void MalformedJson_ReadsNull()
    {
        var path = WriteClaudeJson("{ this is not json ");
        Assert.Null(ClaudeJsonReader.ReadLiveOrg(path));
    }

    [Fact]
    public void SignedOut_NoOAuthAccount_ReadsNull()
    {
        // A pristine, unauthenticated Claude Code writes a .claude.json with no oauthAccount.
        var path = WriteClaudeJson("""{ "projects": {}, "numStartups": 1 }""");
        Assert.Null(ClaudeJsonReader.ReadLiveOrg(path));
    }

    [Fact]
    public void PersonalAccount_NoOrgUuid_ReadsNull()
    {
        // Signed in, but the account carries no organization — nothing to key an Org on.
        var path = WriteClaudeJson("""
            { "oauthAccount": { "emailAddress": "me@example.com" } }
            """);
        Assert.Null(ClaudeJsonReader.ReadLiveOrg(path));
    }

    [Fact]
    public void ReadSignIn_Org_PopulatesOrgAndEmail()
    {
        var path = WriteClaudeJson("""
            { "oauthAccount": { "organizationUuid": "u1", "organizationName": "Org A", "emailAddress": "me@example.com" } }
            """);

        var s = ClaudeJsonReader.ReadSignIn(path);

        Assert.Equal(SignInState.Org, s.State);
        Assert.Equal("u1", s.Org!.Uuid);
        Assert.Equal("Org A", s.Org.Name);
        Assert.Equal("me@example.com", s.Email);
    }

    [Fact]
    public void ReadSignIn_Personal_NoOrg_KeepsEmail()
    {
        var path = WriteClaudeJson("""
            { "oauthAccount": { "emailAddress": "me@example.com" } }
            """);

        var s = ClaudeJsonReader.ReadSignIn(path);

        Assert.Equal(SignInState.Personal, s.State);
        Assert.Null(s.Org);
        Assert.Equal("me@example.com", s.Email);
    }

    [Fact]
    public void ReadSignIn_SignedOut_IsNone()
    {
        var path = WriteClaudeJson("""{ "projects": {}, "numStartups": 1 }""");
        Assert.Equal(ClaudeSignIn.None, ClaudeJsonReader.ReadSignIn(path));

        Assert.Equal(ClaudeSignIn.None, ClaudeJsonReader.ReadSignIn(Path.Combine(_dir, "nope", ".claude.json")));
    }

    [Fact]
    public void ReadSignIn_Dir_FallsBackToParent_WhenInsideStubHasNoAccount()
    {
        // Mimics the DEFAULT ~/.claude: an account-less stub inside, the real login one level up.
        var root = Path.Combine(_dir, ".claude");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, ".claude.json"), """{ "numStartups": 3 }""");          // stub, no oauthAccount
        File.WriteAllText(Path.Combine(_dir, ".claude.json"),
            """{ "oauthAccount": { "organizationUuid": "u1", "organizationName": "Org A" } }""");     // real, in parent

        var s = ClaudeJsonReader.ReadSignIn(new ClaudeConfigDir(root));

        Assert.Equal(SignInState.Org, s.State);
        Assert.Equal("Org A", s.Org!.Name);
    }

    [Fact]
    public void ReadSignIn_Dir_PrefersInside_WhenSignedIn()
    {
        // Env-dir shape (CLAUDE_CONFIG_DIR set): the real login is INSIDE; the parent must not override it.
        var envs = Path.Combine(_dir, "envs");
        var root = Path.Combine(envs, "acme");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, ".claude.json"),
            """{ "oauthAccount": { "organizationUuid": "acme1", "organizationName": "Acme Corp" } }""");
        File.WriteAllText(Path.Combine(envs, ".claude.json"),
            """{ "oauthAccount": { "organizationUuid": "other", "organizationName": "Other" } }""");

        var s = ClaudeJsonReader.ReadSignIn(new ClaudeConfigDir(root));

        Assert.Equal("acme1", s.Org!.Uuid); // inside wins over the parent
    }

    [Fact]
    public void OrgEquality_IsByUuid_NameIsDisplayOnly()
    {
        // Same uuid, different display name (a rename) => the same org.
        var renamed = new Org("u1") { Name = "Acme" };
        var later = new Org("u1") { Name = "Acme Corp" };
        Assert.Equal(renamed, later);
        Assert.Equal(renamed.GetHashCode(), later.GetHashCode());

        Assert.NotEqual(new Org("u1"), new Org("u2"));
    }

    [Fact]
    public void Org_DisplayName_FallsBackToUuid()
    {
        Assert.Equal("u1", new Org("u1").DisplayName);
        Assert.Equal("u1", new Org("u1") { Name = "   " }.DisplayName);
    }
}

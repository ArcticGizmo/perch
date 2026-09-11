using Perch.Data;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// Discovery of the config-directory set, against throwaway fixture trees. Each case is a shape
/// observed on a real multi-environment machine, including the two a naive probe gets wrong: an
/// environment never signed into, and config dirs linked onto one physical directory.
/// </summary>
public class ClaudeConfigDiscoveryTests : IDisposable
{
    private readonly string _home;

    public ClaudeConfigDiscoveryTests()
    {
        _home = Path.Combine(Path.GetTempPath(), "perch-cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_home);
    }

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); } catch { /* best-effort */ }
    }

    private string Hub(params string[] markers) => MakeConfigDir(Path.Combine(_home, ".claude"), markers);

    private string Env(string slug, params string[] markers) =>
        MakeConfigDir(Path.Combine(_home, ".claude-envs", "envs", slug), markers);

    private static string MakeConfigDir(string root, string[] markers)
    {
        Directory.CreateDirectory(root);
        foreach (var marker in markers)
        {
            if (marker.EndsWith(".json") || marker.EndsWith(".sh"))
                File.WriteAllText(Path.Combine(root, marker), "{}");
            else
                Directory.CreateDirectory(Path.Combine(root, marker));
        }
        return root;
    }

    private void WriteManifest(string body, string fileName = "envs.json") =>
        File.WriteAllText(
            Path.Combine(Directory.CreateDirectory(Path.Combine(_home, ".claude-envs")).FullName, fileName),
            body);

    private ClaudeConfigDir Primary(string root) => new(root, root, isHub: true);

    private IReadOnlyList<ClaudeConfigDir> Discover(Func<string, string>? links = null) =>
        ClaudeConfigDiscovery.Discover(_home, Primary(Path.Combine(_home, ".claude")), links);

    [Fact]
    public void NoEnvironments_YieldsOnlyThePrimary()
    {
        Hub("sessions", "projects");

        var dirs = Discover();

        var only = Assert.Single(dirs);
        Assert.Equal(Path.Combine(_home, ".claude"), only.Root);
        Assert.True(only.IsHub);
    }

    [Fact]
    public void PrimaryIsAlwaysIncluded_EvenWhenItDoesNotExistYet()
    {
        var dirs = Discover();
        Assert.Single(dirs);
    }

    [Fact]
    public void Manifest_IsAlsoReadFromItsCurrentName()
    {
        // The manifest was renamed envs.json -> manifest.json. It supplies labels and declared orgs
        // only, never membership, so missing it costs display detail and reports nothing wrong - which
        // is exactly how the rename went unnoticed on a live machine.
        Hub("sessions");
        Env("inflight", "sessions", ".claude.json");
        WriteManifest("""
            { "shared": ["projects", "sessions", "plugins"],
              "environments": [ { "slug": "inflight", "label": "InFlight", "product": "inflight",
                                  "org": "Redux InFlight" } ] }
            """, "manifest.json");

        var env = Assert.Single(Discover(), d => d.Slug == "inflight");
        Assert.Equal("InFlight", env.Label);
        Assert.Equal("Redux InFlight", env.DeclaredOrg);
    }

    [Fact]
    public void Manifest_SuppliesLabelAndDeclaredOrg()
    {
        Hub("sessions");
        Env("inflight", "sessions", ".claude.json");
        WriteManifest("""
            { "envRoot": "~/.claude-envs/envs",
              "environments": [ { "slug": "inflight", "label": "InFlight",
                                  "account": "me@example.com", "org": "Redux InFlight" } ] }
            """);

        var env = Assert.Single(Discover(), d => d.Slug == "inflight");
        Assert.Equal("InFlight", env.Label);
        Assert.Equal("Redux InFlight", env.DeclaredOrg);
        Assert.Equal("me@example.com", env.DeclaredAccount);
    }

    [Fact]
    public void ManifestListingAnEnvironmentThatIsNotOnDisk_IsIgnored()
    {
        Hub("sessions");
        WriteManifest("""
            { "environments": [ { "slug": "ghost", "label": "Ghost", "org": "Nowhere" } ] }
            """);

        Assert.DoesNotContain(Discover(), d => d.Slug == "ghost");
    }

    [Fact]
    public void MalformedManifest_StillFindsTheDirectoriesByProbing()
    {
        Hub("sessions");
        Env("inflight", "sessions");
        WriteManifest("{ this is not json");

        Assert.Contains(Discover(), d => d.Slug == "inflight");
    }

    [Fact]
    public void NoManifestAtAll_StillFindsEnvironmentsByProbing()
    {
        Hub("sessions");
        Env("inflight", "sessions", ".claude.json");
        Env("pdg", "settings.json", "projects");

        var dirs = Discover();

        Assert.Contains(dirs, d => d.Slug == "inflight");
        Assert.Contains(dirs, d => d.Slug == "pdg");
    }

    [Theory]
    [InlineData(true, new[] { "sessions", ".claude.json", "settings.json", "projects" })]
    [InlineData(true, new[] { "sessions" })]
    [InlineData(true, new[] { ".claude.json" })]
    // Never signed into: settings plus a linked projects tree is all it has. Observed on a real
    // machine, and the case a sessions-or-.claude.json-only probe misses.
    [InlineData(true, new[] { "settings.json", "projects" })]
    // settings.json alone is too weak: plenty of unrelated tools keep one.
    [InlineData(false, new[] { "settings.json" })]
    [InlineData(false, new[] { "config", "mcp-config.json", "system-prompt.md" })]
    [InlineData(false, new string[0])]
    public void LooksLikeConfigDir_RequiresARealMarker(bool expected, string[] markers)
    {
        var dir = MakeConfigDir(Path.Combine(_home, "candidate"), markers);
        Assert.Equal(expected, ClaudeConfigDiscovery.LooksLikeConfigDir(dir));
    }

    [Fact]
    public void UnrelatedClaudePrefixedDirectory_IsNotTreatedAsAConfigDir()
    {
        // A worktree helper seen on the observed machine. The marker test keeps it out, not an
        // exclusion list - a false positive would become a hook-install target.
        Hub("sessions");
        MakeConfigDir(Path.Combine(_home, ".claude-wr", "wr-dev"),
            ["config", "mcp-config.json", "system-prompt.md"]);

        Assert.Single(Discover());
    }

    [Fact]
    public void ConfigDirBesideTheHub_IsFound_SoDiscoveryIsNotTiedToOneScheme()
    {
        // Not the conventional envs/<slug> layout: discovery must not be hard-wired to it.
        Hub("sessions");
        MakeConfigDir(Path.Combine(_home, ".claude-work"), ["sessions", ".claude.json"]);

        Assert.Contains(Discover(), d => d.Root.EndsWith(".claude-work"));
    }

    [Fact]
    public void DirectoriesResolvingToTheSamePlace_AppearOnce()
    {
        // Without de-duplication by resolved path the same sessions surface once per config dir.
        // Junction creation needs elevation on Windows, so the resolver is injected.
        Hub("sessions");
        var real = Env("inflight", "sessions", ".claude.json");
        var alias = MakeConfigDir(Path.Combine(_home, ".claude-envs", "envs", "inflight-alias"),
            ["sessions", ".claude.json"]);

        string Resolve(string path) => path == alias ? real : path;

        var dirs = Discover(Resolve);

        Assert.Single(dirs, d => d.RealRoot == real);
        Assert.DoesNotContain(dirs, d => d.Slug == "inflight-alias");
    }

    [Fact]
    public void HubReadsItsAccountFromOneLevelAboveTheConfigDir()
    {
        // Verified on a real machine: ~/.claude held none while ~/.claude.json carried the account.
        var hub = Hub("sessions");
        File.WriteAllText(Path.Combine(_home, ".claude.json"),
            """{ "oauthAccount": { "organizationName": "Redux InFlight", "emailAddress": "me@example.com" } }""");

        var dir = new ClaudeConfigDir(hub, hub, isHub: true);

        Assert.Equal(Path.Combine(_home, ".claude.json"), dir.ClaudeJsonFile);
        Assert.Equal("Redux InFlight", dir.Account.Org);
        Assert.Equal("me@example.com", dir.Account.Email);
    }

    [Fact]
    public void EnvironmentReadsItsAccountFromInsideTheConfigDir()
    {
        var env = Env("inflight", "sessions");
        File.WriteAllText(Path.Combine(env, ".claude.json"),
            """{ "oauthAccount": { "organizationName": "Redux InTuition", "emailAddress": "me@example.com" } }""");

        var dir = new ClaudeConfigDir(env, env, "inflight");

        Assert.Equal(Path.Combine(env, ".claude.json"), dir.ClaudeJsonFile);
        Assert.Equal("Redux InTuition", dir.Account.Org);
    }

    [Fact]
    public void OrgState_IsUnknown_WhenThereIsNoAccountFile()
    {
        var env = Env("pdg", "settings.json", "projects");
        var dir = new ClaudeConfigDir(env, env, "pdg", declaredOrg: "Quartex PDG");
        Assert.Equal(OrgState.Unknown, dir.OrgState);
        Assert.Equal("Quartex PDG", dir.Org);   // the declared org is all there is to go on
    }

    [Fact]
    public void OrgState_IsNotSignedIn_WhenTheAccountFileIsAStub()
    {
        // A freshly synced environment writes a stub .claude.json with empty account fields. That is
        // not a misconfiguration and must not read as a mismatch.
        var env = Env("intuition", "settings.json", "projects");
        File.WriteAllText(Path.Combine(env, ".claude.json"), """{ "oauthAccount": {} }""");

        var dir = new ClaudeConfigDir(env, env, "intuition", declaredOrg: "Redux InTuition");
        Assert.Equal(OrgState.NotSignedIn, dir.OrgState);
    }

    [Fact]
    public void OrgState_IsMismatch_OnlyWhenSignedInToADifferentDeclaredOrg()
    {
        var env = Env("inflight", "sessions");
        var path = Path.Combine(env, ".claude.json");

        File.WriteAllText(path, """{ "oauthAccount": { "organizationName": "Redux InFlight" } }""");
        Assert.Equal(OrgState.Match,
            new ClaudeConfigDir(env, env, "inflight", declaredOrg: "Redux InFlight").OrgState);

        // A rewrite must be seen through the write-time cache.
        File.WriteAllText(path, """{ "oauthAccount": { "organizationName": "Somewhere Else" } }""");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(1));
        Assert.Equal(OrgState.Mismatch,
            new ClaudeConfigDir(env, env, "inflight", declaredOrg: "Redux InFlight").OrgState);
    }

    [Fact]
    public void UsageKey_GroupsByAccountAndOrg_NotByDirectory()
    {
        // Same account *and* org report the same numbers, so they are polled once between them; a
        // different org must not be folded in, since account- vs org-scoping is not knowable here.
        var hub = Hub("sessions");
        File.WriteAllText(Path.Combine(_home, ".claude.json"),
            """{ "oauthAccount": { "organizationName": "Org A", "emailAddress": "me@example.com" } }""");
        var a = Env("a", "sessions");
        File.WriteAllText(Path.Combine(a, ".claude.json"),
            """{ "oauthAccount": { "organizationName": "Org A", "emailAddress": "me@example.com" } }""");
        var b = Env("b", "sessions");
        File.WriteAllText(Path.Combine(b, ".claude.json"),
            """{ "oauthAccount": { "organizationName": "Org B", "emailAddress": "me@example.com" } }""");

        var hubDir = new ClaudeConfigDir(hub, hub, isHub: true);
        var aDir = new ClaudeConfigDir(a, a, "a");
        var bDir = new ClaudeConfigDir(b, b, "b");

        Assert.Equal(hubDir.UsageKey, aDir.UsageKey);
        Assert.NotEqual(aDir.UsageKey, bDir.UsageKey);
    }

    [Fact]
    public void EnvironmentAppearingAfterAnEarlierPass_IsFoundByTheNext()
    {
        // Signing into an environment for the first time materialises its directory, so the set has to
        // be re-derived rather than resolved once at start-up. Observed happening mid-session.
        Hub("sessions");
        Assert.Single(Discover());

        Env("intuition", "sessions", ".claude.json");

        Assert.Contains(Discover(), d => d.Slug == "intuition");
    }

    [Fact]
    public void ChangingTheSet_WakesSubscribers()
    {
        // What the session monitor's watcher re-attach and the hook reconcile both hang off.
        var hub = new ClaudeConfigDir(Hub("sessions"), isHub: true);
        var env = new ClaudeConfigDir(Env("inflight", "sessions"), slug: "inflight");

        int fires = 0;
        void OnChanged() => fires++;
        ClaudeConfigSet.Changed += OnChanged;
        try
        {
            ClaudeConfigSet.SetForTesting([hub]);
            int afterFirst = fires;
            ClaudeConfigSet.SetForTesting([hub, env]);
            Assert.True(fires > afterFirst);
            Assert.Equal(2, ClaudeConfigSet.All.Count);
        }
        finally
        {
            ClaudeConfigSet.Changed -= OnChanged;
            ClaudeConfigSet.SetForTesting(null);
        }
    }

    [Fact]
    public void UsageKey_FallsBackToThePath_SoUnidentifiableDirsAreNeverMerged()
    {
        var a = Env("a", "sessions");
        var b = Env("b", "sessions");
        Assert.NotEqual(new ClaudeConfigDir(a, a, "a").UsageKey, new ClaudeConfigDir(b, b, "b").UsageKey);
    }
}

using Perch.Data;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// Covers <see cref="JiraLink"/> — the pure branch-name → Jira deep-link resolver behind the overlay's
/// ticket glyph. All offline string work (no Jira API), so it's fully unit-testable: key extraction, the
/// project-key filter, sub-domain normalisation, and the branch-leaf parse for detached HEAD.
/// </summary>
public class JiraTicketTests
{
    // ---- Resolve: key extraction + URL ---------------------------------------------------------------

    [Theory]
    [InlineData("SFTY-1234-add-audit-log", "SFTY-1234")]
    [InlineData("feature/SFTY-1234-add-thing", "SFTY-1234")]
    [InlineData("SFTY-1234", "SFTY-1234")]
    [InlineData("bugfix/PROJ-7", "PROJ-7")]
    public void Resolve_ExtractsKeyAndBuildsUrl(string branch, string expectedKey)
    {
        var ticket = JiraLink.Resolve(branch, "acme", null);

        Assert.NotNull(ticket);
        Assert.Equal(expectedKey, ticket!.Value.Key);
        Assert.Equal($"https://acme.atlassian.net/browse/{expectedKey}", ticket.Value.Url);
    }

    [Theory]
    [InlineData("main")]
    [InlineData("develop")]
    [InlineData("sfty-1234-lowercase-no-match")]     // keys are uppercase; a lowercase token isn't a key
    [InlineData("release/v1.2.3")]
    [InlineData("A-1")]                               // single-letter project key isn't valid Jira
    public void Resolve_ReturnsNullWhenNoKey(string branch)
    {
        Assert.Null(JiraLink.Resolve(branch, "acme", null));
    }

    [Fact]
    public void Resolve_TakesFirstKeyWhenBranchHasSeveral()
    {
        var ticket = JiraLink.Resolve("SFTY-1-then-PROJ-2", "acme", null);
        Assert.Equal("SFTY-1", ticket!.Value.Key);
    }

    [Fact]
    public void Resolve_DoesNotMatchKeyGluedToAnotherToken()
    {
        Assert.Null(JiraLink.Resolve("xSFTY-1234", "acme", null));
    }

    // ---- Resolve: project-key filter -----------------------------------------------------------------

    [Fact]
    public void Resolve_ProjectFilterSuppressesUnlistedKeys()
    {
        // PROJ isn't in the filter, so the first *matching* key (SFTY-1) is chosen, not PROJ-9.
        var ticket = JiraLink.Resolve("PROJ-9-and-SFTY-1", "acme", "SFTY");
        Assert.Equal("SFTY-1", ticket!.Value.Key);
    }

    [Fact]
    public void Resolve_ProjectFilterWithNoMatchReturnsNull()
    {
        Assert.Null(JiraLink.Resolve("PROJ-9-only", "acme", "SFTY"));
    }

    [Theory]
    [InlineData("SFTY, PROJ")]
    [InlineData("proj sfty")]                          // case-insensitive, whitespace-separated
    [InlineData("PROJ;SFTY")]
    public void Resolve_ProjectFilterAcceptsListedKey(string filter)
    {
        var ticket = JiraLink.Resolve("SFTY-42-thing", "acme", filter);
        Assert.Equal("SFTY-42", ticket!.Value.Key);
    }

    // ---- Sub-domain normalisation --------------------------------------------------------------------

    [Theory]
    [InlineData("acme")]
    [InlineData("acme.atlassian.net")]
    [InlineData("https://acme.atlassian.net")]
    [InlineData("https://acme.atlassian.net/")]
    [InlineData("  acme.atlassian.net/jira/software  ")]
    [InlineData("ACME.ATLASSIAN.NET")]                 // suffix strip is case-insensitive; slug kept verbatim
    public void NormalizeSubdomain_ReducesToSlug(string raw)
    {
        // The slug's case is preserved from the input up to the suffix; assert the URL is well-formed.
        var slug = JiraLink.NormalizeSubdomain(raw);
        Assert.False(string.IsNullOrEmpty(slug));
        Assert.DoesNotContain("/", slug);
        Assert.DoesNotContain("atlassian.net", slug, System.StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".atlassian.net")]                     // nothing left after the suffix
    public void NormalizeSubdomain_ReturnsNullForEmpty(string? raw)
    {
        Assert.Null(JiraLink.NormalizeSubdomain(raw));
    }

    [Fact]
    public void Resolve_ReturnsNullWhenNoSiteConfigured()
    {
        Assert.Null(JiraLink.Resolve("SFTY-1234", null, null));
        Assert.Null(JiraLink.Resolve("SFTY-1234", "   ", null));
    }

    [Fact]
    public void Resolve_AcceptsFullHostAsSubdomain()
    {
        var ticket = JiraLink.Resolve("SFTY-1234", "https://acme.atlassian.net/", null);
        Assert.Equal("https://acme.atlassian.net/browse/SFTY-1234", ticket!.Value.Url);
    }

    // ---- BranchFromHeadRef ---------------------------------------------------------------------------

    [Theory]
    [InlineData("ref: refs/heads/SFTY-1234-add-thing", "SFTY-1234-add-thing")]
    [InlineData("ref: refs/heads/feature/PROJ-7", "feature/PROJ-7")]
    [InlineData("  ref: refs/heads/main  ", "main")]
    public void BranchFromHeadRef_ParsesLeaf(string headRef, string expected)
    {
        Assert.Equal(expected, JiraLink.BranchFromHeadRef(headRef));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("9d3a1c0f8b2e4a6d7c5f0a1b2c3d4e5f60718293")]   // detached HEAD: a raw commit SHA, not a ref
    public void BranchFromHeadRef_ReturnsNullForDetachedOrEmpty(string? headRef)
    {
        Assert.Null(JiraLink.BranchFromHeadRef(headRef));
    }

    [Fact]
    public void EndToEnd_HeadRefToTicket()
    {
        var branch = JiraLink.BranchFromHeadRef("ref: refs/heads/SFTY-9001-warp-speed");
        var ticket = JiraLink.Resolve(branch, "acme.atlassian.net", "SFTY");

        Assert.Equal("SFTY-9001", ticket!.Value.Key);
        Assert.Equal("https://acme.atlassian.net/browse/SFTY-9001", ticket.Value.Url);
    }
}

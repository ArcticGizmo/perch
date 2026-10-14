using Perch.Data;
using Xunit;

namespace Perch.Tests;

// The ledger's root is a static seam, so these run one at a time.
[Collection(nameof(PublishLedgerTests))]
public class PublishLedgerTests : IDisposable
{
    private const string Parent = "5b4d131d-dfd4-4103-860c-f5c96094b598";
    private readonly string _root = Directory.CreateTempSubdirectory("perch-ledger-").FullName;

    public PublishLedgerTests() => PublishLedger.RootOverride = _root;

    public void Dispose()
    {
        PublishLedger.RootOverride = null;
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void A_publish_in_flight_is_pending_until_it_ends()
    {
        PublishLedger.BeginPublish("715e4caa", Parent, @"C:\work\artefacts\page.html");
        var p = Assert.Single(PublishLedger.Pending());
        Assert.Equal(("715e4caa", Parent), (p.ShortId, p.ParentSessionId));

        var row = Assert.Single(PublishLedger.PendingRowsFor(PublishLedger.Pending(), Parent));
        Assert.Equal("Publishing page.html as an artifact", row.Description);
        Assert.Empty(PublishLedger.PendingRowsFor(PublishLedger.Pending(), "a30ccded-624a-4e78-aba8-44b5502581b2"));

        PublishLedger.EndPublish("715e4caa");
        Assert.Empty(PublishLedger.Pending());
    }

    [Theory]
    [InlineData("../escape", Parent)]
    [InlineData("715e4caa", "not a session; id")]
    public void Unusable_ids_are_never_written(string shortId, string parent)
    {
        PublishLedger.BeginPublish(shortId, parent, @"C:\work\page.html");
        Assert.Empty(PublishLedger.Pending());
    }

    [Fact]
    public void Published_artifacts_are_recorded_against_the_parent_and_deduplicated()
    {
        PublishLedger.RecordPublished(Parent, new PublishedArtifact("https://claude.ai/artifact/aaa", "p", "First", false));
        PublishLedger.RecordPublished(Parent, new PublishedArtifact("https://claude.ai/artifact/bbb", "p", "Second", false));
        PublishLedger.RecordPublished(Parent, new PublishedArtifact("https://claude.ai/artifact/aaa", "p", "First, renamed", false));
        PublishLedger.RecordPublished(Parent, new PublishedArtifact("https://evil.example/artifact/x", "p", "Nope", false));

        var list = PublishLedger.PublishedFor(Parent);
        Assert.Equal(["https://claude.ai/artifact/aaa", "https://claude.ai/artifact/bbb"], list.Select(a => a.Url));
        Assert.Equal("First, renamed", list[0].Title);
        Assert.Empty(PublishLedger.PublishedFor("a30ccded-624a-4e78-aba8-44b5502581b2"));
    }

    [Fact]
    public void Ledger_artifacts_merge_after_the_transcripts_without_duplicates()
    {
        var fromTranscript = new List<Artifact> { new("https://claude.ai/artifact/aaa", "A") };
        var fromLedger = new List<Artifact> { new("https://claude.ai/artifact/aaa", "A again"), new("https://claude.ai/artifact/bbb", "B") };
        var merged = PublishLedger.Merge(fromTranscript, fromLedger);
        Assert.Equal(["A", "B"], merged.Select(a => a.Title));
    }
}

using System.Text.Json.Nodes;
using Perch.Social;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// The Phoenix-frame building and parsing that <see cref="SupabaseRealtimeConnection"/> relies on, pinned
/// without a socket. The parser must be strict (only a real <c>public.posts</c> INSERT fires the callback) and
/// unshakeable (any other or malformed frame is ignored, never thrown), because a false negative just falls
/// back to the poll while a thrown exception would kill the receive loop.
/// </summary>
public sealed class RealtimeProtocolTests
{
    [Theory]
    [InlineData("https://abc.supabase.co", "wss://abc.supabase.co/realtime/v1/websocket")]
    [InlineData("https://abc.supabase.co/", "wss://abc.supabase.co/realtime/v1/websocket")]
    [InlineData("http://localhost:54321", "ws://localhost:54321/realtime/v1/websocket")]
    public void SocketUri_maps_scheme_and_path(string baseUrl, string expectedPrefix)
    {
        var uri = RealtimeProtocol.SocketUri(baseUrl, "pub-key");
        Assert.StartsWith(expectedPrefix, uri.ToString());
        Assert.Contains("apikey=pub-key", uri.Query);
        Assert.Contains("vsn=1.0.0", uri.Query);
    }

    [Fact]
    public void JoinPosts_frame_subscribes_to_posts_inserts_with_token()
    {
        var frame = JsonNode.Parse(RealtimeProtocol.JoinPosts(1, "jwt-123"))!.AsObject();
        Assert.Equal(RealtimeProtocol.PostsTopic, (string?)frame["topic"]);
        Assert.Equal("phx_join", (string?)frame["event"]);
        Assert.Equal("jwt-123", (string?)frame["payload"]!["access_token"]);
        var change = frame["payload"]!["config"]!["postgres_changes"]!.AsArray()[0]!;
        Assert.Equal("INSERT", (string?)change["event"]);
        Assert.Equal("posts", (string?)change["table"]);
    }

    [Fact]
    public void TryParseInsert_reads_a_posts_insert()
    {
        var id = Guid.NewGuid();
        var author = Guid.NewGuid();
        var record = $$"""{"id":"{{id}}","author":"{{author}}","body":"hello","mood_emoji":"🎉","created_at":"2026-08-19T10:00:00Z"}""";
        var json =
            """{"event":"postgres_changes","topic":"realtime:public:posts","payload":{"data":{"schema":"public","table":"posts","type":"INSERT","record":"""
            + record + "}}}";

        Assert.True(RealtimeProtocol.TryParseInsert(json, out var post));
        Assert.Equal(id, post.Id);
        Assert.Equal(author, post.Author);
        Assert.Equal("hello", post.Body);
        Assert.Equal("🎉", post.Mood);
    }

    [Theory]
    [InlineData("""{"event":"phx_reply","payload":{"status":"ok"}}""")]                                  // a join ack, not a change
    [InlineData("""{"event":"postgres_changes","payload":{"data":{"table":"posts","type":"UPDATE","record":{}}}}""")] // not an INSERT
    [InlineData("""{"event":"postgres_changes","payload":{"data":{"table":"friendships","type":"INSERT","record":{}}}}""")] // wrong table
    [InlineData("""{"event":"postgres_changes","payload":{"data":{"table":"posts","type":"INSERT","record":{"id":"not-a-guid"}}}}""")]
    [InlineData("not json at all")]
    [InlineData("")]
    public void TryParseInsert_ignores_everything_else(string json)
    {
        Assert.False(RealtimeProtocol.TryParseInsert(json, out _));
    }
}

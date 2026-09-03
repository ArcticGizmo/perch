using System.IO.Pipes;
using System.Text;
using Perch.Data.Control;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// The `perch` CLI → tray control pipe (docs/session-ui-plan.md, Phase 3): a real named-pipe round-trip
/// against <see cref="ControlServer"/> the way <c>Program.ForwardSessionIntent</c> drives it — one intent
/// line in, one reply line back per connection.
/// </summary>
public class ControlServerTests
{
    private static string UniquePipe() => "perch-control-test-" + Guid.NewGuid().ToString("N");

    private static async Task<string> RoundTrip(ControlServer server, string pipeName, string payload)
    {
        server.Start();
        await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(5000);
        var bytes = Encoding.UTF8.GetBytes(payload + "\n");
        await client.WriteAsync(bytes);
        await client.FlushAsync();
        using var reader = new StreamReader(client, Encoding.UTF8, leaveOpen: true);
        return await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)) ?? "";
    }

    [Fact]
    public async Task Intent_ReachesHandler_AndReplyComesBack()
    {
        var pipe = UniquePipe();
        SessionOpenIntent? seen = null;
        using var server = new ControlServer(pipe)
        {
            Handle = i => { seen = i; return Task.FromResult(new ControlReply(true, "Opening in Perch.")); },
        };
        var intent = new SessionOpenIntent(@"C:\proj", "abcdef12-3456-7890-abcd-ef1234567890", Model: "opus");

        var reply = ControlReply.Parse(await RoundTrip(server, pipe, intent.ToJson()));

        Assert.NotNull(reply);
        Assert.True(reply!.Ok);
        Assert.Equal("Opening in Perch.", reply.Message);
        Assert.Equal(intent, seen);
    }

    [Fact]
    public async Task Garbage_AnswersNotOk()
    {
        var pipe = UniquePipe();
        using var server = new ControlServer(pipe) { Handle = _ => throw new InvalidOperationException("must not be called") };
        var reply = ControlReply.Parse(await RoundTrip(server, pipe, "not json"));
        Assert.NotNull(reply);
        Assert.False(reply!.Ok);
    }

    [Fact]
    public async Task HandlerThrow_IsReportedNotSwallowed()
    {
        var pipe = UniquePipe();
        using var server = new ControlServer(pipe) { Handle = _ => throw new InvalidOperationException("boom") };
        var reply = ControlReply.Parse(await RoundTrip(server, pipe, new SessionOpenIntent(@"C:\proj").ToJson()));
        Assert.NotNull(reply);
        Assert.False(reply!.Ok);
        Assert.Contains("boom", reply.Message);
    }
}

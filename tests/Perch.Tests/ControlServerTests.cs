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
    // A folder that exists: Parse refuses an intent for one that doesn't (review fixes CP13).
    private static readonly string Here = AppContext.BaseDirectory.TrimEnd('\\', '/');

    // Kept short: on macOS/Linux .NET backs the pipe with a Unix socket at $TMPDIR/CoreFxPipe_<name>, and the
    // whole path must fit in 104 bytes (macOS's TMPDIR alone is ~50).
    private static string UniquePipe() => "perch-t-" + Guid.NewGuid().ToString("N")[..12];

    // The client exactly as Program.ForwardSessionIntent opens it: current-user-only (so Connect verifies the
    // server's owner) and identification-only impersonation.
    private static NamedPipeClientStream Client(string pipeName) =>
        new(".", pipeName, PipeDirection.InOut, ControlProtocol.Options, ControlProtocol.ClientImpersonation);

    private static async Task<string> RoundTrip(ControlServer server, string pipeName, string payload)
    {
        server.Start();
        await using var client = Client(pipeName);
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
        var intent = new SessionOpenIntent(Here, "abcdef12-3456-7890-abcd-ef1234567890", Model: "opus");

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
        var reply = ControlReply.Parse(await RoundTrip(server, pipe, new SessionOpenIntent(Here).ToJson()));
        Assert.NotNull(reply);
        Assert.False(reply!.Ok);
        Assert.Contains("boom", reply.Message);
    }

    // CP10: a request line past the cap is refused without being handed to the handler (or buffered whole).
    [Fact]
    public async Task OversizedLine_IsRefused_HandlerNeverCalled()
    {
        var pipe = UniquePipe();
        bool called = false;
        using var server = new ControlServer(pipe) { Handle = _ => { called = true; return Task.FromResult(new ControlReply(true, "no")); } };
        server.Start();

        await using var client = Client(pipe);
        await client.ConnectAsync(5000);
        var huge = Encoding.UTF8.GetBytes(new string('x', ControlProtocol.MaxLineBytes + 8 * 1024) + "\n");
        // The server stops reading at the cap, answers, and hangs up — so the tail of this write fails. Don't await
        // it before reading the reply, or it would wait on a reader that has gone.
        var write = client.WriteAsync(huge).AsTask();
        using var reader = new StreamReader(client, Encoding.UTF8, leaveOpen: true);
        var reply = ControlReply.Parse(await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)) ?? "");
        try { await write; } catch { /* broken pipe once the server hung up */ }

        Assert.NotNull(reply);
        Assert.False(reply!.Ok);
        Assert.Contains("too large", reply.Message);
        Assert.False(called);
    }

    // CP10: a client that connects and sends nothing is dropped after the read timeout, not held forever.
    [Fact]
    public async Task SilentClient_IsDroppedAfterTheReadTimeout()
    {
        var pipe = UniquePipe();
        using var server = new ControlServer(pipe, readTimeout: TimeSpan.FromMilliseconds(200))
        {
            Handle = _ => throw new InvalidOperationException("must not be called"),
        };
        server.Start();

        await using var client = Client(pipe);
        await client.ConnectAsync(5000);
        using var reader = new StreamReader(client, Encoding.UTF8, leaveOpen: true);
        // The server hangs up with no reply: end of stream, well inside the 5s guard.
        Assert.Null(await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
    }

    // CP10: if the name is held when the tray starts (a previous instance tearing down, or a squatter), the server
    // retries instead of giving up for the tray's lifetime — and answers once the name frees up.
    [Fact]
    public async Task HeldPipeName_IsRetried_UntilItFrees()
    {
        var pipe = UniquePipe();
        var blocker = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1);   // one instance max: ours can't join
        using var server = new ControlServer(pipe, firstBackoff: TimeSpan.FromMilliseconds(50))
        {
            Handle = _ => Task.FromResult(new ControlReply(true, "Opening in Perch.")),
        };
        server.Start();
        await Task.Delay(300);   // several failed creations + backoffs
        await blocker.DisposeAsync();

        await using var client = Client(pipe);
        await client.ConnectAsync(5000);   // waits for the server's next retry to create the pipe
        await client.WriteAsync(Encoding.UTF8.GetBytes(new SessionOpenIntent(Here).ToJson() + "\n"));
        using var reader = new StreamReader(client, Encoding.UTF8, leaveOpen: true);
        var reply = ControlReply.Parse(await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)) ?? "");
        Assert.True(reply?.Ok);
    }

    [Fact]
    public void PipeName_IsPerProfileAndPerUser()
    {
        Assert.Equal("perch-control-S-1-5-21-1", ControlProtocol.PipeNameFor(false, "S-1-5-21-1"));
        Assert.Equal("perch-control-dev-S-1-5-21-1", ControlProtocol.PipeNameFor(true, "S-1-5-21-1"));

        var name = ControlProtocol.PipeName;
        Assert.Matches("^perch-control-(dev-)?[A-Za-z0-9_-]+$", name);
        if (OperatingSystem.IsWindows())
            Assert.EndsWith("-" + System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value, name);
    }

    [Fact]
    public void HardenedOptions_AreCurrentUserOnly_AndNeverImpersonate()
    {
        Assert.True(ControlProtocol.Options.HasFlag(PipeOptions.CurrentUserOnly));
        Assert.Equal(System.Security.Principal.TokenImpersonationLevel.Identification, ControlProtocol.ClientImpersonation);
    }

    // ── the bounded line reader ────────────────────────────────────────────────────────
    private static Task<string?> ReadLine(string text, int max = 16) =>
        ControlProtocol.ReadLineAsync(new MemoryStream(Encoding.UTF8.GetBytes(text)), max, CancellationToken.None);

    [Theory]
    [InlineData("hello\n", "hello")]
    [InlineData("hello\r\n", "hello")]
    [InlineData("hello\nignored after", "hello")]
    [InlineData("partial", "partial")]              // stream ended before a newline
    [InlineData("0123456789abcdef\n", "0123456789abcdef")]   // exactly the cap
    public async Task ReadLine_ReturnsTheFirstLine(string text, string expected) =>
        Assert.Equal(expected, await ReadLine(text));

    [Fact]
    public async Task ReadLine_EmptyStream_IsNull() => Assert.Null(await ReadLine(""));

    [Theory]
    [InlineData("0123456789abcdefX\n")]           // one byte over
    [InlineData("0123456789abcdef0123456789")]     // over, and no newline at all
    public async Task ReadLine_PastTheCap_Throws(string text) =>
        await Assert.ThrowsAsync<InvalidDataException>(() => ReadLine(text));
}

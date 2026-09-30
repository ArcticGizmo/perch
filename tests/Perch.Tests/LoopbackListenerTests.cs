using System.Net.Sockets;
using System.Text;
using Perch.Social;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// <see cref="LoopbackListener"/> (review fixes CP16): the OAuth redirect catcher must not be won by whichever
/// connection arrives first. Anything that isn't <c>GET /callback</c> with a code or an error gets a 404 and the
/// wait goes on; a connection that sends nothing is dropped after the read timeout.
/// </summary>
public sealed class LoopbackListenerTests
{
    [Theory]
    [InlineData("GET /callback?code=abc HTTP/1.1", true)]
    [InlineData("GET /callback?error=access_denied&error_description=no HTTP/1.1", true)]
    [InlineData("GET /callback HTTP/1.1", false)]                 // no code, no error
    [InlineData("GET /callback?code= HTTP/1.1", false)]           // empty code
    [InlineData("GET /favicon.ico HTTP/1.1", false)]
    [InlineData("GET /callbackx?code=abc HTTP/1.1", false)]
    [InlineData("GET /callback/extra?code=abc HTTP/1.1", false)]
    [InlineData("POST /callback?code=abc HTTP/1.1", false)]
    [InlineData("", false)]
    public void Only_the_real_redirect_counts_as_the_callback(string requestLine, bool expected) =>
        Assert.Equal(expected, LoopbackListener.IsCallback(requestLine, out _));

    // The candidates must sit below Windows' dynamic port range (49152+): Hyper-V / WSL / Docker reserve blocks
    // inside it, and the old 53682-53685 fell in one, so sign-in couldn't bind at all on those machines. Every
    // candidate must also be in Supabase's Redirect URLs allowlist (see backend/supabase/README.md).
    [Fact]
    public void Candidate_ports_stay_below_the_dynamic_range()
    {
        Assert.Equal([41532, 41533, 41534, 41535], LoopbackListener.CandidatePorts);
        Assert.All(LoopbackListener.CandidatePorts, p => Assert.InRange(p, 1024, 49151));
    }

    [Fact]
    public void The_real_candidates_bind_on_this_host()
    {
        using var listener = LoopbackListener.Start();
        Assert.Contains(listener.Port, LoopbackListener.CandidatePorts);
        Assert.Equal($"http://127.0.0.1:{listener.Port}/callback", listener.RedirectUri);
    }

    [Fact]
    public async Task Stray_and_silent_connections_do_not_end_the_wait()
    {
        var saved = LoopbackListener.RequestReadTimeout;
        LoopbackListener.RequestReadTimeout = TimeSpan.FromMilliseconds(300);
        try
        {
            using var listener = LoopbackListener.Start([0]);   // any free port
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var wait = listener.WaitForCallbackAsync(cts.Token);

            // A stray request first: it gets a 404 and the wait carries on.
            Assert.StartsWith("HTTP/1.1 404", await SendAsync(listener.Port, "GET /favicon.ico HTTP/1.1\r\n\r\n"));
            Assert.False(wait.IsCompleted);

            // A client that connects and says nothing is dropped after the read timeout.
            using (var silent = new TcpClient())
            {
                await silent.ConnectAsync("127.0.0.1", listener.Port);
                await Task.Delay(600);
                Assert.False(wait.IsCompleted);
            }

            // The real redirect completes it.
            Assert.StartsWith("HTTP/1.1 200", await SendAsync(listener.Port, "GET /callback?code=the-code HTTP/1.1\r\n\r\n"));
            var query = await wait;
            Assert.Equal("the-code", query["code"]);
        }
        finally { LoopbackListener.RequestReadTimeout = saved; }
    }

    private static async Task<string> SendAsync(int port, string request)
    {
        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", port);
        using var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request));
        var buffer = new byte[4096];
        int n = await stream.ReadAsync(buffer);
        return Encoding.ASCII.GetString(buffer, 0, n);
    }
}

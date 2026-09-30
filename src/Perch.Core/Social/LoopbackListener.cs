using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Perch.Social;

/// <summary>
/// A minimal loopback HTTP catcher for the OAuth redirect. Binds <c>127.0.0.1</c> on the first free port
/// from a small candidate list, waits for the single browser <c>GET /callback?code=…</c> that Supabase
/// redirects to, replies with a tiny "you can close this tab" page, and hands back the parsed query.
///
/// A raw <see cref="TcpListener"/> (not <see cref="HttpListener"/>) on purpose: HttpListener needs a URL-ACL
/// reservation for non-admin users on Windows, which a desktop app can't assume; a loopback TCP socket has
/// no such requirement and behaves the same on every OS. Reads only the request line — enough to pull the
/// query string — then closes.
/// </summary>
internal sealed class LoopbackListener : IDisposable
{
    // Candidate ports, tried in order; every one must be in Supabase's Redirect URLs allowlist
    // (http://127.0.0.1:<port>/callback), or GoTrue silently redirects to the Site URL instead. Keep them stable.
    //
    // They sit below Windows' dynamic port range (49152-65535 by default). Hyper-V / WSL / Docker reserve
    // blocks inside that range, which can move on reboot; the old 53682-53685 fell in one (53588-53687), so
    // sign-in couldn't bind a port at all on such machines. Keep any new port below 49152 too.
    public static readonly int[] CandidatePorts = [41532, 41533, 41534, 41535];

    private readonly TcpListener _listener;
    public int Port { get; }
    public string RedirectUri => $"http://127.0.0.1:{Port}/callback";

    private LoopbackListener(TcpListener listener, int port)
    {
        _listener = listener;
        Port = port;
    }

    /// <summary>Binds the first free candidate port. Throws <see cref="SocialException"/> if all are taken.</summary>
    public static LoopbackListener Start() => Start(CandidatePorts);

    /// <summary>Binds the first free port of <paramref name="ports"/> (0 = any free port; tests use that, since
    /// the real candidates can sit inside a Windows excluded port range on some machines).</summary>
    internal static LoopbackListener Start(IEnumerable<int> ports)
    {
        foreach (var port in ports)
        {
            try
            {
                var l = new TcpListener(IPAddress.Loopback, port);
                l.Start();
                return new LoopbackListener(l, ((IPEndPoint)l.LocalEndpoint).Port);
            }
            catch (SocketException) { /* port busy — try the next */ }
        }
        throw new SocialException("Couldn't open a local port to complete sign-in. Close other apps and retry.");
    }

    // How long one connection gets to send its request line before it's dropped, so a client that connects and
    // says nothing can't hold the listener while the real browser redirect waits behind it.
    internal static TimeSpan RequestReadTimeout { get; set; } = TimeSpan.FromSeconds(5);   // settable for tests

    /// <summary>
    /// Waits for the browser callback and returns its query parameters (<c>code</c>, or <c>error</c>). Honours
    /// <paramref name="ct"/>.
    /// <para>Keeps accepting until a connection is the real callback — <c>GET /callback</c> carrying a
    /// <c>code</c> or an <c>error</c> — and answers anything else (a port scan, a favicon fetch, a web page
    /// poking 127.0.0.1) with a 404 without ending the wait (review fixes CP16). The first connection used to
    /// win, so any local process or page could fail the sign-in by getting there first. A forged callback
    /// that <em>does</em> carry a code still can't sign anyone in: the code has to redeem against this
    /// sign-in's PKCE verifier.</para>
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> WaitForCallbackAsync(CancellationToken ct)
    {
        using var reg = ct.Register(() => { try { _listener.Stop(); } catch { } });
        while (true)
        {
            using var client = await _listener.AcceptTcpClientAsync(ct);
            using var stream = client.GetStream();

            string firstLine;
            using (var read = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                read.CancelAfter(RequestReadTimeout);
                try { firstLine = await ReadRequestLineAsync(stream, read.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { continue; }   // stalled: drop it
                catch (IOException) { continue; }
            }

            if (!IsCallback(firstLine, out var query))
            {
                try { await stream.WriteAsync(NotFound, ct); } catch (IOException) { }
                continue;
            }

            const string body = "<!doctype html><html><body style='font:16px sans-serif;padding:3rem;text-align:center'>"
                              + "<h2>Perch is signed in</h2><p>You can close this tab and return to Perch.</p></body></html>";
            string resp = "HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\n"
                        + $"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}";
            try { await stream.WriteAsync(Encoding.UTF8.GetBytes(resp), ct); } catch (IOException) { }
            return query;
        }
    }

    private static readonly byte[] NotFound =
        Encoding.ASCII.GetBytes("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");

    // Reads up to the end of the request line (or 8 KB, whichever comes first).
    private static async Task<string> ReadRequestLineAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[8192];
        int total = 0;
        while (total < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(total), ct);
            if (n == 0) break;
            total += n;
            if (Array.IndexOf(buffer, (byte)'\n', 0, total) >= 0) break;
        }
        return Encoding.ASCII.GetString(buffer, 0, total).Split('\n', 2)[0].TrimEnd('\r');
    }

    /// <summary>True when <paramref name="requestLine"/> is the OAuth redirect: a <c>GET</c> of exactly
    /// <c>/callback</c> whose query carries a non-empty <c>code</c> or <c>error</c>. Pure; unit-tested.</summary>
    internal static bool IsCallback(string requestLine, out IReadOnlyDictionary<string, string> query)
    {
        query = ParseRequestLineQuery(requestLine);
        var parts = requestLine.Split(' ');
        if (parts.Length < 2 || parts[0] != "GET") return false;
        var path = parts[1];
        int q = path.IndexOf('?');
        if ((q < 0 ? path : path[..q]) != "/callback") return false;
        return query.TryGetValue("code", out var c) && c.Length > 0
            || query.TryGetValue("error", out var e) && e.Length > 0;
    }

    /// <summary>Parses the query of an HTTP request line ("GET /callback?code=x&amp;state=y HTTP/1.1").</summary>
    public static IReadOnlyDictionary<string, string> ParseRequestLineQuery(string requestLine)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        var parts = requestLine.Split(' ');
        if (parts.Length < 2) return dict;

        int q = parts[1].IndexOf('?');
        if (q < 0) return dict;

        foreach (var kv in parts[1][(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int i = kv.IndexOf('=');
            if (i <= 0) continue;
            dict[Uri.UnescapeDataString(kv[..i])] = Uri.UnescapeDataString(kv[(i + 1)..]);
        }
        return dict;
    }

    public void Dispose() { try { _listener.Stop(); } catch { } }
}

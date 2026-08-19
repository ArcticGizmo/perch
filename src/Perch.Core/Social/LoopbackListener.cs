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
    // Candidate ports. The first is the one to allowlist in Supabase's Redirect URLs; the rest are only
    // used if it's momentarily busy. Keep the primary stable so the allowlist entry keeps working.
    public static readonly int[] CandidatePorts = [53682, 53683, 53684, 53685];

    private readonly TcpListener _listener;
    public int Port { get; }
    public string RedirectUri => $"http://127.0.0.1:{Port}/callback";

    private LoopbackListener(TcpListener listener, int port)
    {
        _listener = listener;
        Port = port;
    }

    /// <summary>Binds the first free candidate port. Throws <see cref="SocialException"/> if all are taken.</summary>
    public static LoopbackListener Start()
    {
        foreach (var port in CandidatePorts)
        {
            try
            {
                var l = new TcpListener(IPAddress.Loopback, port);
                l.Start();
                return new LoopbackListener(l, port);
            }
            catch (SocketException) { /* port busy — try the next */ }
        }
        throw new SocialException("Couldn't open a local port to complete sign-in. Close other apps and retry.");
    }

    /// <summary>Waits for the browser callback and returns its query parameters (e.g. <c>code</c>, or
    /// <c>error</c>). Honours <paramref name="ct"/>.</summary>
    public async Task<IReadOnlyDictionary<string, string>> WaitForCallbackAsync(CancellationToken ct)
    {
        using var reg = ct.Register(() => { try { _listener.Stop(); } catch { } });
        using var client = await _listener.AcceptTcpClientAsync(ct);
        using var stream = client.GetStream();

        var buffer = new byte[8192];
        int n = await stream.ReadAsync(buffer, ct);
        string firstLine = Encoding.ASCII.GetString(buffer, 0, n).Split('\n', 2)[0];
        var query = ParseRequestLineQuery(firstLine);

        const string body = "<!doctype html><html><body style='font:16px sans-serif;padding:3rem;text-align:center'>"
                          + "<h2>Perch is signed in</h2><p>You can close this tab and return to Perch.</p></body></html>";
        string resp = "HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\n"
                    + $"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}";
        await stream.WriteAsync(Encoding.UTF8.GetBytes(resp), ct);
        return query;
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

using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;

namespace Perch.Remote;

/// <summary>
/// The controller's side of a worker connection (docs/remote-workers-plan.md, W2): connect to a paired worker, or pair
/// with a new one. The worker's certificate must match the pin saved when they paired; while pairing there is no pin
/// yet, so the user compares the six-digit code shown on both machines instead.
/// </summary>
internal static class WorkerClient
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PairingTimeout = TimeSpan.FromMinutes(2);

    /// <summary>Connects to a paired worker and exchanges hellos. The returned link isn't receiving yet. Throws
    /// <see cref="RemoteLinkException"/> with a message for the user.</summary>
    public static async Task<RemoteLink> ConnectAsync(PairedDevice worker, X509Certificate2 identity, string deviceName,
        CancellationToken ct = default)
    {
        var link = await OpenAsync(worker.Host ?? "", worker.Port, identity, worker.Pin, ct).ConfigureAwait(false);
        try
        {
            await link.SendAsync(WorkerServer.Hello(deviceName), ct).ConfigureAwait(false);
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            wait.CancelAfter(ConnectTimeout);
            var reply = await link.ReadFrameAsync(wait.Token).ConfigureAwait(false);
            if (RemoteLink.Str(reply, "t") != "hello") throw new RemoteLinkException(MessageOf(reply, "the worker didn't answer"));
            if (WorkerServer.VersionOf(reply) != RemoteLink.Version)
                throw new RemoteLinkException("the worker runs a different Perch protocol; update both machines");
            link.PeerName = RemoteLink.CleanName(RemoteLink.Str(reply, "name") ?? worker.Name);
            return link;
        }
        catch (Exception ex)
        {
            link.Close();
            throw Wrap(ex);
        }
    }

    /// <summary>Pairs with a worker that has pairing open. <paramref name="showCode"/> gets the six digits to compare
    /// with the worker's screen as soon as the connection is up; the call returns once the worker's user allows it
    /// (the new <see cref="PairedDevice"/>, role Worker) and throws <see cref="RemoteLinkException"/> otherwise.</summary>
    public static async Task<PairedDevice> PairAsync(string host, int port, X509Certificate2 identity, string deviceName,
        Action<string> showCode, CancellationToken ct = default)
    {
        var link = await OpenAsync(host, port, identity, expectedPin: null, ct).ConfigureAwait(false);
        try
        {
            var pair = RemoteLink.Frame("pair");
            pair["v"] = RemoteLink.Version;
            pair["name"] = deviceName;
            await link.SendAsync(pair, ct).ConfigureAwait(false);
            showCode(DeviceTrust.PairingCode(DeviceTrust.PinOf(identity), link.PeerPin));

            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            wait.CancelAfter(PairingTimeout + TimeSpan.FromSeconds(10));
            var reply = await link.ReadFrameAsync(wait.Token).ConfigureAwait(false);
            if (RemoteLink.Str(reply, "t") != "paired")
                throw new RemoteLinkException(MessageOf(reply, "the worker closed the connection"));
            return new PairedDevice
            {
                Name = RemoteLink.CleanName(RemoteLink.Str(reply, "name")),
                Pin = link.PeerPin,
                Role = PairedRole.Worker,
                Host = host,
                Port = port,
                AddedUtc = DateTime.UtcNow,
            };
        }
        catch (Exception ex)
        {
            throw Wrap(ex);
        }
        finally
        {
            link.Close();
        }
    }

    private static async Task<RemoteLink> OpenAsync(string host, int port, X509Certificate2 identity, string? expectedPin,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(host)) throw new RemoteLinkException("no address for this worker");
        var tcp = new TcpClient();
        using var connect = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connect.CancelAfter(ConnectTimeout);
        try
        {
            await tcp.ConnectAsync(host.Trim(), port, connect.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            tcp.Dispose();
            throw new RemoteLinkException(
                ex is OperationCanceledException ? $"no answer from {host}:{port}" : $"couldn't reach {host}:{port} ({ex.Message})", ex);
        }

        string? serverPin = null;
        var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false, (_, certificate, _, _) =>
        {
            if (certificate is null) return false;
            serverPin = DeviceTrust.PinOf(certificate);
            return expectedPin is null || string.Equals(serverPin, expectedPin, StringComparison.Ordinal);
        });
        try
        {
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "perch-worker",
                ClientCertificates = [identity],
                EnabledSslProtocols = SslProtocols.Tls13 | SslProtocols.Tls12,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            }, connect.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            var message = expectedPin is not null && serverPin is not null && serverPin != expectedPin
                ? $"{host} isn't the machine you paired with (its key changed); pair it again if you reset it"
                : expectedPin is null
                    ? $"{host} refused: open pairing on it first (Settings → Workers)"
                    : $"{host} refused the connection: is this machine still paired there?";
            throw new RemoteLinkException(message, ex);
        }
        return new RemoteLink(ssl, serverPin!);
    }

    // The worker's refusal, shown to the user: printable characters only, and short.
    private static string MessageOf(JsonObject? frame, string fallback)
    {
        string? msg;
        try { msg = RemoteLink.Str(frame, "msg"); }
        catch { msg = null; }
        if (string.IsNullOrWhiteSpace(msg)) return fallback;
        var clean = new string(msg.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return clean.Length > 200 ? clean[..200] : clean;
    }

    private static RemoteLinkException Wrap(Exception ex) => ex switch
    {
        RemoteLinkException r => r,
        OperationCanceledException => new RemoteLinkException("the worker didn't answer in time", ex),
        _ => new RemoteLinkException($"the connection failed ({ex.Message})", ex),
    };
}

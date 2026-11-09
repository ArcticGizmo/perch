using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;

namespace Perch.Remote;

/// <summary>A controller asking to pair, shown to the user on the worker with the code to compare.</summary>
internal sealed record PairingRequest(string Name, string Code, string Pin);

/// <summary>
/// The worker's listener (docs/remote-workers-plan.md, W2). A connection must present a certificate this machine
/// pinned when it paired, checked inside the TLS handshake, so nothing a stranger sends is ever read. The one
/// exception is the pairing window the user opens: then an unknown certificate may connect, but can only ask to pair,
/// and only the user's Allow on this machine (after comparing the six-digit code) pins it.
/// </summary>
internal sealed class WorkerServer : IDisposable
{
    private const int MaxConnections = 8;
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PairingAnswerTimeout = TimeSpan.FromMinutes(2);

    private readonly X509Certificate2 _identity;
    private readonly string _ownPin;
    private readonly Func<string> _deviceName;
    private readonly Func<string, PairedDevice?> _findController;
    private TcpListener? _listener;
    private CancellationTokenSource? _stopping;
    private int _connections;
    private DateTime _pairingUntilUtc;
    private int _pairingBusy;

    /// <param name="identity">This machine's certificate, private key attached.</param>
    /// <param name="deviceName">This machine's name as controllers see it (read at each connection).</param>
    /// <param name="findController">The paired controller with this pin, or null.</param>
    public WorkerServer(X509Certificate2 identity, Func<string> deviceName, Func<string, PairedDevice?> findController)
    {
        _identity = identity;
        _ownPin = DeviceTrust.PinOf(identity);
        _deviceName = deviceName;
        _findController = findController;
    }

    /// <summary>Asks the user whether to pair (raised off the UI thread; the handler marshals). Unset denies.</summary>
    public Func<PairingRequest, Task<bool>>? ApprovePairing { get; set; }

    /// <summary>Why a paired controller may not connect right now (the user just disconnected it), or null to let it in.
    /// Checked after its hello, so it hears the reason.</summary>
    public Func<string, string?>? RefuseReason { get; set; }

    /// <summary>The user allowed a controller: persist it.</summary>
    public event Action<PairedDevice>? Paired;

    /// <summary>A paired controller connected and said hello. The link isn't receiving yet: the handler wires
    /// <see cref="RemoteLink.FrameReceived"/> and calls <see cref="RemoteLink.StartReceiving"/>.</summary>
    public event Action<RemoteLink>? ControllerConnected;

    public int Port => (_listener?.LocalEndpoint as IPEndPoint)?.Port ?? 0;
    public bool IsListening => _listener is not null;
    public bool PairingOpen => DateTime.UtcNow < _pairingUntilUtc;

    /// <summary>Starts listening. Throws <see cref="SocketException"/> when the address or port can't be bound.</summary>
    public void Start(IPAddress address, int port)
    {
        if (_listener is not null) return;
        var listener = new TcpListener(address, port);
        // "Every interface" covers IPv4 too, so a LAN or Tailscale 100.x address reaches it.
        if (address.Equals(IPAddress.IPv6Any)) listener.Server.DualMode = true;
        listener.Start();
        _listener = listener;
        _stopping = new CancellationTokenSource();
        var token = _stopping.Token;
        Task.Run(() => AcceptLoop(listener, token));
    }

    public void Stop()
    {
        _stopping?.Cancel();
        try { _listener?.Stop(); } catch { /* already stopped */ }
        _listener = null;
        _pairingUntilUtc = default;
    }

    public void OpenPairing(TimeSpan window) => _pairingUntilUtc = DateTime.UtcNow + window;
    public void ClosePairing() => _pairingUntilUtc = default;

    public void Dispose() => Stop();

    private async Task AcceptLoop(TcpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient tcp;
            try { tcp = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false); }
            catch { return; }   // stopped
            if (Interlocked.Increment(ref _connections) > MaxConnections)
            {
                Interlocked.Decrement(ref _connections);
                tcp.Dispose();
                continue;
            }
            _ = Task.Run(async () =>
            {
                try { await Handle(tcp, token).ConfigureAwait(false); }
                catch { tcp.Dispose(); }
                finally { Interlocked.Decrement(ref _connections); }
            }, CancellationToken.None);
        }
    }

    private async Task Handle(TcpClient tcp, CancellationToken stopping)
    {
        string? peerPin = null;
        var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false, (_, certificate, _, _) =>
        {
            if (certificate is null) return false;
            peerPin = DeviceTrust.PinOf(certificate);
            return _findController(peerPin) is not null || PairingOpen;
        });
        try
        {
            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(stopping);
            handshake.CancelAfter(HandshakeTimeout);
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = _identity,
                ClientCertificateRequired = true,
                EnabledSslProtocols = SslProtocols.Tls13 | SslProtocols.Tls12,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            }, handshake.Token).ConfigureAwait(false);
        }
        catch
        {
            await ssl.DisposeAsync().ConfigureAwait(false);   // refused in the handshake: nothing was read
            return;
        }

        var link = new RemoteLink(ssl, peerPin!);
        JsonObject? first;
        try
        {
            using var firstFrame = CancellationTokenSource.CreateLinkedTokenSource(stopping);
            firstFrame.CancelAfter(HandshakeTimeout);
            first = await link.ReadFrameAsync(firstFrame.Token).ConfigureAwait(false);
        }
        catch
        {
            link.Close();
            return;
        }

        var type = RemoteLink.Str(first, "t");
        if (type == "hello" && _findController(link.PeerPin) is { } controller)
        {
            if (VersionOf(first!) != RemoteLink.Version)
            {
                await Refuse(link, $"this machine runs a different Perch protocol (v{RemoteLink.Version}); update both");
                return;
            }
            if (RefuseReason?.Invoke(link.PeerPin) is { } why)
            {
                await Refuse(link, why);
                return;
            }
            link.PeerName = RemoteLink.CleanName(RemoteLink.Str(first, "name") ?? controller.Name);
            await link.SendAsync(Hello(_deviceName())).ConfigureAwait(false);
            ControllerConnected?.Invoke(link);
            return;
        }
        if (type == "pair")
        {
            await HandlePairing(link, first!).ConfigureAwait(false);
            return;
        }
        await Refuse(link, type == "hello" ? "this machine isn't paired with yours" : "unexpected message");
    }

    private async Task HandlePairing(RemoteLink link, JsonObject request)
    {
        if (!PairingOpen || ApprovePairing is not { } approve)
        {
            await Refuse(link, "pairing isn't open on this machine: open it from Settings → Workers");
            return;
        }
        if (Interlocked.Exchange(ref _pairingBusy, 1) != 0)
        {
            await Refuse(link, "another machine is pairing right now");
            return;
        }
        try
        {
            if (VersionOf(request) != RemoteLink.Version)
            {
                await Refuse(link, $"this machine runs a different Perch protocol (v{RemoteLink.Version}); update both");
                return;
            }
            var name = RemoteLink.CleanName(RemoteLink.Str(request, "name"));
            var code = DeviceTrust.PairingCode(_ownPin, link.PeerPin);
            var answer = approve(new PairingRequest(name, code, link.PeerPin));
            bool allowed = await Task.WhenAny(answer, Task.Delay(PairingAnswerTimeout)).ConfigureAwait(false) == answer
                           && answer.Result;
            if (!allowed)
            {
                await Refuse(link, "pairing was declined on the other machine", "pair_denied");
                return;
            }
            ClosePairing();
            Paired?.Invoke(new PairedDevice
            {
                Name = name, Pin = link.PeerPin, Role = PairedRole.Controller, AddedUtc = DateTime.UtcNow,
            });
            var done = RemoteLink.Frame("paired");
            done["name"] = _deviceName();
            await link.SendAsync(done).ConfigureAwait(false);
            link.Close();
        }
        finally
        {
            Volatile.Write(ref _pairingBusy, 0);
        }
    }

    private static async Task Refuse(RemoteLink link, string message, string type = "error")
    {
        var frame = RemoteLink.Frame(type);
        frame["msg"] = message;
        try { await link.SendAsync(frame).ConfigureAwait(false); }
        catch { /* the peer may be gone */ }
        link.Close();
    }

    internal static JsonObject Hello(string name)
    {
        var hello = RemoteLink.Frame("hello");
        hello["v"] = RemoteLink.Version;
        hello["name"] = name;
        return hello;
    }

    internal static int VersionOf(JsonObject? frame)
    {
        try { return frame?["v"]?.GetValue<int>() ?? 0; }
        catch { return 0; }
    }
}

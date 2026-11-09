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
    // Connections still in the handshake (anyone can open one) and connections from paired controllers are counted
    // apart, so strangers stalling handshakes can't lock a paired controller out of an open slot; one address can hold
    // only a few of the handshake slots, so one stranger can't take them all either (W6).
    internal const int MaxPending = 32;
    internal const int MaxPendingPerAddress = 4;
    internal const int MaxConnected = 16;
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PairingAnswerTimeout = TimeSpan.FromMinutes(2);

    private readonly X509Certificate2 _identity;
    private readonly string _ownPin;
    private readonly Func<string> _deviceName;
    private readonly Func<string, PairedDevice?> _findController;
    private TcpListener? _listener;
    private CancellationTokenSource? _stopping;
    private int _pending, _connected;
    private readonly Dictionary<IPAddress, int> _pendingByAddress = [];
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
            var address = AddressOf(tcp);
            if (!TakePendingSlot(address))
            {
                tcp.Dispose();
                continue;
            }
            _ = Task.Run(async () =>
            {
                try { await Handle(tcp, token).ConfigureAwait(false); }
                catch { tcp.Dispose(); }
                finally { ReleasePendingSlot(address); }
            }, CancellationToken.None);
        }
    }

    // The peer's address, an IPv4 one as itself rather than mapped into IPv6 (the dual-mode listener reports both ways).
    private static IPAddress AddressOf(TcpClient tcp)
    {
        var address = (tcp.Client.RemoteEndPoint as IPEndPoint)?.Address ?? IPAddress.None;
        return address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
    }

    private bool TakePendingSlot(IPAddress address)
    {
        lock (_pendingByAddress)
        {
            int fromAddress = _pendingByAddress.GetValueOrDefault(address);
            if (_pending >= MaxPending || fromAddress >= MaxPendingPerAddress) return false;
            _pending++;
            _pendingByAddress[address] = fromAddress + 1;
            return true;
        }
    }

    private void ReleasePendingSlot(IPAddress address)
    {
        lock (_pendingByAddress)
        {
            _pending--;
            if (_pendingByAddress.TryGetValue(address, out var n) && n > 1) _pendingByAddress[address] = n - 1;
            else _pendingByAddress.Remove(address);
        }
    }

    /// <summary>Handshakes in progress now, for tests.</summary>
    internal int PendingCount { get { lock (_pendingByAddress) return _pending; } }

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
                await Refuse(link, RefuseVersion(first));
                return;
            }
            if (RefuseReason?.Invoke(link.PeerPin) is { } why)
            {
                await Refuse(link, why);
                return;
            }
            if (Interlocked.Increment(ref _connected) > MaxConnected)
            {
                Interlocked.Decrement(ref _connected);
                await Refuse(link, "this machine has too many controllers connected");
                return;
            }
            link.Closed += _ => Interlocked.Decrement(ref _connected);
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
                await Refuse(link, RefuseVersion(request));
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
        hello["app"] = Perch.Data.AppInfo.Version;
        return hello;
    }

    /// <summary>What a controller tells its user when the worker speaks another protocol version: both Perch versions,
    /// since updating is the fix.</summary>
    internal static string VersionMismatch(string otherName, JsonObject? otherHello) =>
        $"{otherName} runs Perch {AppOf(otherHello)} and this machine runs {Perch.Data.AppInfo.Version}, which can't talk " +
        "to each other; update both to the same version";

    // The worker's refusal of a controller on another protocol version, worded for the controller's screen.
    private string RefuseVersion(JsonObject? theirHello) =>
        $"{_deviceName()} runs Perch {Perch.Data.AppInfo.Version} and this machine runs {AppOf(theirHello)}, which can't " +
        "talk to each other; update both to the same version";

    // The Perch version a hello announces, made safe to show; one from before versions were announced says so.
    private static string AppOf(JsonObject? hello) =>
        RemoteLink.Str(hello, "app") is { Length: > 0 and <= 40 } app
            ? new string(app.Where(c => !char.IsControl(c)).ToArray())
            : "an older version";

    internal static int VersionOf(JsonObject? frame)
    {
        try { return frame?["v"]?.GetValue<int>() ?? 0; }
        catch { return 0; }
    }
}

using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using Perch.Remote;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// Remote workers' transport (docs/remote-workers-plan.md, W2): the pairing code, framing, and pairing/connecting over
/// real TLS on loopback. The TLS tests need a device key SChannel can use, made here as the Windows store makes it, so
/// they run on Windows only (the macOS head doesn't offer remote workers yet).
/// </summary>
public class RemoteWorkerTests
{
    [Fact]
    public void PairingCode_IsTheSameFromEitherSide_AndSixDigits()
    {
        var a = new string('A', 64);
        var b = new string('B', 64);
        var code = DeviceTrust.PairingCode(a, b);
        Assert.Equal(code, DeviceTrust.PairingCode(b, a));
        Assert.Matches(@"^\d{3} \d{3}$", code);
        Assert.NotEqual(code, DeviceTrust.PairingCode(a, new string('C', 64)));
    }

    [Fact]
    public async Task Frames_ReadInOrder_AndEndCleanly()
    {
        var link = new RemoteLink(new MemoryStream(Encoding.UTF8.GetBytes("{\"t\":\"a\"}\n{\"t\":\"b\",\"n\":2}\n")), "pin");
        Assert.Equal("a", RemoteLink.Str(await link.ReadFrameAsync(), "t"));
        Assert.Equal("b", RemoteLink.Str(await link.ReadFrameAsync(), "t"));
        Assert.Null(await link.ReadFrameAsync());
    }

    [Theory]
    [InlineData("[1,2]\n")]                 // not an object
    [InlineData("{\"x\":1}\n")]             // no type
    [InlineData("{\"t\":3}\n")]             // type isn't a string
    [InlineData("not json\n")]
    [InlineData("{\"t\":\"cut off")]        // closed mid-message
    public async Task BadFrames_EndTheConnection(string wire)
    {
        var link = new RemoteLink(new MemoryStream(Encoding.UTF8.GetBytes(wire)), "pin");
        await Assert.ThrowsAsync<InvalidDataException>(() => link.ReadFrameAsync());
    }

    [Fact]
    public async Task OversizedFrame_IsRefused()
    {
        var big = new byte[RemoteLink.MaxFrameBytes + 10];
        Array.Fill(big, (byte)'x');
        var link = new RemoteLink(new MemoryStream(big), "pin");
        await Assert.ThrowsAsync<InvalidDataException>(() => link.ReadFrameAsync());
    }

    [Fact]
    public void PeerNames_AreMadeSafe()
    {
        Assert.Equal("laptop", RemoteLink.CleanName("  lap\u0007top \n"));
        Assert.Equal("unnamed machine", RemoteLink.CleanName(null));
        Assert.Equal(64, RemoteLink.CleanName(new string('n', 200)).Length);
    }

    // ── Liveness and limits (W6) ───────────────────────────────────────────────

    [Fact]
    public async Task SilentPeer_IsDropped()
    {
        var (mine, theirs) = await SocketPair();
        using var _ = theirs;   // never reads or writes: a peer that went to sleep
        var link = new RemoteLink(mine, "pin")
        {
            HeartbeatEvery = TimeSpan.FromMilliseconds(50), DropAfterSilence = TimeSpan.FromMilliseconds(300),
        };
        var closed = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        link.Closed += r => closed.TrySetResult(r);
        link.StartReceiving();
        Assert.Contains("no answer", await closed.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task IdlePeers_StayUp_OnPingsTheyNeverSee()
    {
        var (a, b) = await SocketPair();
        RemoteLink Link(Stream s) => new(s, "pin")
        {
            HeartbeatEvery = TimeSpan.FromMilliseconds(50), DropAfterSilence = TimeSpan.FromMilliseconds(400),
        };
        using var left = Link(a);
        using var right = Link(b);
        int frames = 0;
        left.FrameReceived += _ => Interlocked.Increment(ref frames);
        right.FrameReceived += _ => Interlocked.Increment(ref frames);
        left.StartReceiving();
        right.StartReceiving();
        await Task.Delay(1200);
        Assert.False(left.IsClosed);
        Assert.False(right.IsClosed);
        Assert.Equal(0, frames);
    }

    // One address can only hold a few of the handshake slots, so a stranger stalling handshakes can't fill them all.
    [Fact]
    public async Task StalledHandshakes_FromOneAddress_AreCapped()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var cert = new CertificateRequest("CN=Perch", key, HashAlgorithmName.SHA256)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        using var server = new WorkerServer(cert, () => "worker-box", _ => null);
        server.Start(IPAddress.Loopback, 0);

        var stalled = new List<System.Net.Sockets.TcpClient>();
        try
        {
            for (int i = 0; i < WorkerServer.MaxPendingPerAddress; i++)
            {
                var c = new System.Net.Sockets.TcpClient();
                await c.ConnectAsync(IPAddress.Loopback, server.Port);
                stalled.Add(c);
            }
            for (int i = 0; i < 100 && server.PendingCount < WorkerServer.MaxPendingPerAddress; i++) await Task.Delay(20);
            Assert.Equal(WorkerServer.MaxPendingPerAddress, server.PendingCount);

            using var extra = new System.Net.Sockets.TcpClient();
            await extra.ConnectAsync(IPAddress.Loopback, server.Port);
            var read = extra.GetStream().ReadAsync(new byte[1]).AsTask();
            Assert.Equal(0, await read.WaitAsync(TimeSpan.FromSeconds(5)));   // turned away at once
            Assert.Equal(WorkerServer.MaxPendingPerAddress, server.PendingCount);
        }
        finally
        {
            foreach (var c in stalled) c.Dispose();
        }
    }

    [Fact]
    public void VersionMismatch_NamesBothVersions()
    {
        var hello = RemoteLink.Frame("hello");
        hello["app"] = "9.9.9";
        var message = WorkerServer.VersionMismatch("studio-pc", hello);
        Assert.Contains("studio-pc runs Perch 9.9.9", message);
        Assert.Contains(Perch.Data.AppInfo.Version, message);
        Assert.Contains("an older version", WorkerServer.VersionMismatch("studio-pc", RemoteLink.Frame("hello")));
    }

    private static async Task<(Stream, Stream)> SocketPair()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var client = new System.Net.Sockets.TcpClient();
            var accept = listener.AcceptTcpClientAsync();
            await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            return ((await accept).GetStream(), client.GetStream());
        }
        finally
        {
            listener.Stop();
        }
    }

    // ── Over TLS (Windows) ─────────────────────────────────────────────────────

    [Fact]
    public async Task Pairing_Allowed_PinsBothSides_WithTheSameCode()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var worker = new TestIdentity();
        using var controller = new TestIdentity();
        using var server = new WorkerServer(worker.Cert, () => "worker-box", _ => null);
        PairingRequest? asked = null;
        PairedDevice? pinnedOnWorker = null;
        server.ApprovePairing = req => { asked = req; return Task.FromResult(true); };
        server.Paired += d => pinnedOnWorker = d;
        server.Start(IPAddress.Loopback, 0);
        server.OpenPairing(TimeSpan.FromMinutes(1));

        string? shownOnController = null;
        var pinnedOnController = await WorkerClient.PairAsync("127.0.0.1", server.Port, controller.Cert, "controller-box",
            code => shownOnController = code);

        Assert.Equal(worker.Pin, pinnedOnController.Pin);
        Assert.Equal(PairedRole.Worker, pinnedOnController.Role);
        Assert.Equal("worker-box", pinnedOnController.Name);
        Assert.Equal(server.Port, pinnedOnController.Port);

        Assert.NotNull(pinnedOnWorker);
        Assert.Equal(controller.Pin, pinnedOnWorker!.Pin);
        Assert.Equal(PairedRole.Controller, pinnedOnWorker.Role);
        Assert.Equal("controller-box", asked!.Name);
        Assert.Equal(asked.Code, shownOnController);
        Assert.False(server.PairingOpen);   // one pairing per window
    }

    [Fact]
    public async Task Pairing_Declined_Fails()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var worker = new TestIdentity();
        using var controller = new TestIdentity();
        using var server = new WorkerServer(worker.Cert, () => "worker-box", _ => null);
        server.ApprovePairing = _ => Task.FromResult(false);
        server.Start(IPAddress.Loopback, 0);
        server.OpenPairing(TimeSpan.FromMinutes(1));

        var ex = await Assert.ThrowsAsync<RemoteLinkException>(() =>
            WorkerClient.PairAsync("127.0.0.1", server.Port, controller.Cert, "c", _ => { }));
        Assert.Contains("declined", ex.Message);
    }

    // Outside a pairing window an unknown machine is turned away inside the TLS handshake.
    [Fact]
    public async Task Stranger_IsRefusedInTheHandshake()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var worker = new TestIdentity();
        using var stranger = new TestIdentity();
        using var server = new WorkerServer(worker.Cert, () => "worker-box", _ => null);
        server.ApprovePairing = _ => Task.FromResult(true);
        server.Start(IPAddress.Loopback, 0);

        await Assert.ThrowsAsync<RemoteLinkException>(() =>
            WorkerClient.PairAsync("127.0.0.1", server.Port, stranger.Cert, "s", _ => { }));
        var asWorker = new PairedDevice { Host = "127.0.0.1", Port = server.Port, Pin = worker.Pin, Role = PairedRole.Worker };
        await Assert.ThrowsAsync<RemoteLinkException>(() => WorkerClient.ConnectAsync(asWorker, stranger.Cert, "s"));
    }

    [Fact]
    public async Task PairedController_Connects_AndFramesFlowBothWays()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var worker = new TestIdentity();
        using var controller = new TestIdentity();
        var known = new PairedDevice { Name = "controller-box", Pin = controller.Pin, Role = PairedRole.Controller };
        using var server = new WorkerServer(worker.Cert, () => "worker-box", pin => pin == known.Pin ? known : null);
        server.ControllerConnected += link =>
        {
            link.FrameReceived += f =>
            {
                var reply = RemoteLink.Frame("reply");
                reply["echo"] = RemoteLink.Str(f, "say");
                link.Send(reply);
            };
            link.StartReceiving();
        };
        server.Start(IPAddress.Loopback, 0);

        var asWorker = new PairedDevice { Host = "127.0.0.1", Port = server.Port, Pin = worker.Pin, Role = PairedRole.Worker };
        using var client = await WorkerClient.ConnectAsync(asWorker, controller.Cert, "controller-box");
        Assert.Equal("worker-box", client.PeerName);

        var got = new TaskCompletionSource<JsonObject>();
        client.FrameReceived += f => got.TrySetResult(f);
        client.StartReceiving();
        var message = RemoteLink.Frame("message");   // not "ping": the heartbeat keeps that one to itself
        message["say"] = "hi";
        await client.SendAsync(message);

        var reply = await got.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("reply", RemoteLink.Str(reply, "t"));
        Assert.Equal("hi", RemoteLink.Str(reply, "echo"));
    }

    // A controller the worker's user just disconnected is turned away after its hello, and told why.
    [Fact]
    public async Task HeldOffController_HearsWhy()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var worker = new TestIdentity();
        using var controller = new TestIdentity();
        var known = new PairedDevice { Name = "c", Pin = controller.Pin, Role = PairedRole.Controller };
        using var server = new WorkerServer(worker.Cert, () => "worker-box", pin => pin == known.Pin ? known : null)
        {
            RefuseReason = pin => pin == controller.Pin ? "worker-box disconnected this machine" : null,
        };
        server.Start(IPAddress.Loopback, 0);

        var asWorker = new PairedDevice { Host = "127.0.0.1", Port = server.Port, Pin = worker.Pin, Role = PairedRole.Worker };
        var ex = await Assert.ThrowsAsync<RemoteLinkException>(() => WorkerClient.ConnectAsync(asWorker, controller.Cert, "c"));
        Assert.Contains("disconnected this machine", ex.Message);
    }

    // The controller pinned the worker's key: a different machine answering at that address is refused.
    [Fact]
    public async Task WorkerWithAnotherKey_IsRefused()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var impostor = new TestIdentity();
        using var controller = new TestIdentity();
        var known = new PairedDevice { Name = "c", Pin = controller.Pin, Role = PairedRole.Controller };
        using var server = new WorkerServer(impostor.Cert, () => "impostor", pin => pin == known.Pin ? known : null);
        server.Start(IPAddress.Loopback, 0);

        var expected = new PairedDevice { Host = "127.0.0.1", Port = server.Port, Pin = new string('0', 64), Role = PairedRole.Worker };
        var ex = await Assert.ThrowsAsync<RemoteLinkException>(() => WorkerClient.ConnectAsync(expected, controller.Cert, "c"));
        Assert.Contains("isn't the machine you paired with", ex.Message);
    }

    // A device key made the way the Windows store makes it (a named CNG key; SChannel can't use an ephemeral one),
    // deleted again afterwards.
    private sealed class TestIdentity : IDisposable
    {
        private readonly string _name = "Perch-Test-" + Guid.NewGuid().ToString("N");

        public TestIdentity()
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            using var key = CngKey.Create(CngAlgorithm.ECDsaP256, _name, new CngKeyCreationParameters
            {
                ExportPolicy = CngExportPolicies.None,
                Provider = CngProvider.MicrosoftSoftwareKeyStorageProvider,
            });
            using var ecdsa = new ECDsaCng(key);
            var request = new CertificateRequest("CN=Perch", ecdsa, HashAlgorithmName.SHA256);
            Cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
            Pin = DeviceTrust.PinOf(Cert);
        }

        public X509Certificate2 Cert { get; }
        public string Pin { get; }

        public void Dispose()
        {
            Cert.Dispose();
            if (!OperatingSystem.IsWindows()) return;
            try
            {
                using var key = CngKey.Open(_name);
                key.Delete();
            }
            catch (CryptographicException) { /* already gone */ }
        }
    }
}

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
                var pong = RemoteLink.Frame("pong");
                pong["echo"] = RemoteLink.Str(f, "say");
                link.Send(pong);
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
        var ping = RemoteLink.Frame("ping");
        ping["say"] = "hi";
        await client.SendAsync(ping);

        var reply = await got.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("pong", RemoteLink.Str(reply, "t"));
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

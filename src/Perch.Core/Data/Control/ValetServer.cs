using System.IO.Pipes;
using System.Text;

namespace Perch.Data.Control;

/// <summary>
/// The tray side of the permission valet (see <see cref="ValetProtocol"/>): a named-pipe server that
/// accepts one request line per connection from <c>perch-hook valet</c>, asks <see cref="Decide"/> for
/// a verdict, and writes the reply line. Fail-open by construction — any error, a null
/// <see cref="Decide"/>, or an unparseable request answers <c>pass</c>, which leaves Claude Code's
/// normal permission flow untouched. Connections are handled concurrently (one hook per in-flight tool
/// call, across any number of sessions). <see cref="Decide"/> runs off the UI thread; marshal inside.
/// </summary>
internal sealed class ValetServer : IDisposable
{
    /// <summary>The verdict callback. Keep the fast path fast: return <see cref="ValetDecision.Pass"/>
    /// immediately unless UI is genuinely about to be shown — the hook (and so the session's tool call)
    /// is blocked until this resolves.</summary>
    public Func<ValetRequest, Task<ValetDecision>>? Decide { get; set; }

    private readonly string _pipeName;
    private readonly CancellationTokenSource _cts = new();

    public ValetServer(string? pipeName = null) => _pipeName = pipeName ?? ValetProtocol.PipeName;

    public void Start() => Task.Run(AcceptLoop);

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            NamedPipeServerStream server;
            try
            {
                server = new NamedPipeServerStream(
                    _pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            }
            catch
            {
                return; // pipe name unavailable (another instance owns it) — valet stays off
            }

            try
            {
                await server.WaitForConnectionAsync(_cts.Token).ConfigureAwait(false);
            }
            catch
            {
                await server.DisposeAsync().ConfigureAwait(false);
                if (_cts.IsCancellationRequested) return;
                continue;
            }

            _ = Task.Run(() => Handle(server));
        }
    }

    private async Task Handle(NamedPipeServerStream pipe)
    {
        await using var _ = pipe;
        try
        {
            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            var line = await reader.ReadLineAsync(_cts.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(line)) return;

            var decision = ValetDecision.Pass;
            if (ValetRequest.Parse(line) is { } request && Decide is { } decide)
            {
                try { decision = await decide(request).ConfigureAwait(false); }
                catch { decision = ValetDecision.Pass; }
            }

            var reply = Encoding.UTF8.GetBytes(ValetProtocol.ReplyJson(decision.Kind, decision.Reason) + "\n");
            await pipe.WriteAsync(reply, _cts.Token).ConfigureAwait(false);
            await pipe.FlushAsync(_cts.Token).ConfigureAwait(false);
        }
        catch
        {
            // Hook disconnected / cancelled — it fails open on its side too.
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        // A pending WaitForConnectionAsync observes the token; per-connection streams dispose in Handle.
    }
}

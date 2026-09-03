using System.IO.Pipes;
using System.Text;

namespace Perch.Data.Control;

/// <summary>
/// The tray side of <see cref="ControlProtocol"/>: accepts one <see cref="SessionOpenIntent"/> line per
/// connection from a <c>perch</c> CLI launch, asks <see cref="Handle"/> to act on it (the app marshals to
/// the UI thread and opens a session window), and writes the <see cref="ControlReply"/>. Same shape as
/// <see cref="ValetServer"/>: any failure answers a not-ok reply and never throws out of the accept loop.
/// </summary>
internal sealed class ControlServer : IDisposable
{
    public Func<SessionOpenIntent, Task<ControlReply>>? Handle { get; set; }

    private readonly string _pipeName;
    private readonly CancellationTokenSource _cts = new();

    public ControlServer(string? pipeName = null) => _pipeName = pipeName ?? ControlProtocol.PipeName;

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
                return; // pipe name unavailable (another instance owns it) — the CLI reports "no answer"
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

            _ = Task.Run(() => HandleConnection(server));
        }
    }

    private async Task HandleConnection(NamedPipeServerStream pipe)
    {
        await using var _ = pipe;
        try
        {
            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            var line = await reader.ReadLineAsync(_cts.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(line)) return;

            ControlReply reply;
            if (SessionOpenIntent.Parse(line) is not { } intent) reply = new ControlReply(false, "Perch didn't understand the request.");
            else if (Handle is not { } handle) reply = new ControlReply(false, "Perch isn't accepting session requests right now.");
            else
            {
                try { reply = await handle(intent).ConfigureAwait(false); }
                catch (Exception ex) { reply = new ControlReply(false, $"Perch couldn't open the session: {ex.Message}"); }
            }

            var bytes = Encoding.UTF8.GetBytes(reply.ToJson() + "\n");
            await pipe.WriteAsync(bytes, _cts.Token).ConfigureAwait(false);
            await pipe.FlushAsync(_cts.Token).ConfigureAwait(false);
        }
        catch
        {
            // Client went away; nothing to answer.
        }
    }

    public void Dispose() => _cts.Cancel();
}

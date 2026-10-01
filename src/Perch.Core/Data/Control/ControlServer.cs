using System.IO.Pipes;
using System.Text;

namespace Perch.Data.Control;

/// <summary>
/// The tray side of <see cref="ControlProtocol"/>: accepts one <see cref="SessionOpenIntent"/> line per
/// connection from a <c>perch</c> CLI launch, asks <see cref="Handle"/> to act on it (the app marshals to
/// the UI thread and opens a session window), and writes the <see cref="ControlReply"/>. Any failure answers a
/// not-ok reply and never throws out of the accept loop.
///
/// <para>The pipe is current-user-only (<see cref="ControlProtocol.Options"/>); a client that connects but sends
/// nothing is dropped after <see cref="ControlProtocol.ReadTimeout"/>, and a line past
/// <see cref="ControlProtocol.MaxLineBytes"/> is refused unread. If the pipe can't be created (the name is held
/// — a previous instance still tearing down, or squatted), the loop retries with capped backoff rather than
/// leaving the tray deaf for its whole lifetime (review fixes CP10).</para>
/// </summary>
internal sealed class ControlServer : IDisposable
{
    public Func<SessionOpenIntent, Task<ControlReply>>? Handle { get; set; }

    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    private readonly string _pipeName;
    private readonly TimeSpan _readTimeout;
    private readonly TimeSpan _firstBackoff;
    private readonly CancellationTokenSource _cts = new();

    /// <param name="readTimeout">Test seam; defaults to <see cref="ControlProtocol.ReadTimeout"/>.</param>
    /// <param name="firstBackoff">Test seam: the first retry delay after a failed pipe creation (doubles, capped at 30s).</param>
    public ControlServer(string? pipeName = null, TimeSpan? readTimeout = null, TimeSpan? firstBackoff = null)
    {
        _pipeName = pipeName ?? ControlProtocol.PipeName;
        _readTimeout = readTimeout ?? ControlProtocol.ReadTimeout;
        _firstBackoff = firstBackoff ?? TimeSpan.FromMilliseconds(500);
    }

    public void Start() => Task.Run(AcceptLoop);

    private async Task AcceptLoop()
    {
        int failures = 0;
        while (!_cts.IsCancellationRequested)
        {
            NamedPipeServerStream server;
            try
            {
                server = new NamedPipeServerStream(
                    _pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, ControlProtocol.Options);
                failures = 0;
            }
            catch
            {
                // The name is held (meanwhile the CLI reports "no answer"). Back off and try again.
                var delay = TimeSpan.FromTicks(Math.Min(MaxBackoff.Ticks, _firstBackoff.Ticks << Math.Min(failures++, 16)));
                try { await Task.Delay(delay, _cts.Token).ConfigureAwait(false); }
                catch { return; }
                continue;
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
            string? line;
            bool tooLong = false;
            using (var read = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token))
            {
                read.CancelAfter(_readTimeout);   // a silent client is dropped (OperationCanceledException → catch)
                try { line = await ControlProtocol.ReadLineAsync(pipe, ControlProtocol.MaxLineBytes, read.Token).ConfigureAwait(false); }
                catch (InvalidDataException) { line = null; tooLong = true; }
            }
            if (!tooLong && string.IsNullOrWhiteSpace(line)) return;

            ControlReply reply;
            if (tooLong) reply = new ControlReply(false, "Perch refused the request: it was too large.");
            else if (SessionOpenIntent.Parse(line!) is not { } intent) reply = new ControlReply(false, "Perch didn't understand the request.");
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

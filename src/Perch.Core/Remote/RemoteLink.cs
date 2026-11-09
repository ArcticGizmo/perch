using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Perch.Remote;

/// <summary>A connection that failed in a way worth telling the user about ("the worker refused the pairing").</summary>
internal sealed class RemoteLinkException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// One authenticated connection between two paired Perch machines: newline-delimited JSON frames over a TLS stream
/// (docs/remote-workers-plan.md, W2/W3). Every frame is an object with a string <c>t</c> (its type). A frame over
/// <see cref="MaxFrameBytes"/>, or one that isn't such an object, ends the connection: the peer is paired, so a bad
/// frame is a bug or an attack, not something to recover from.
/// <para>Writes are serialised. <see cref="FrameReceived"/> is raised on the receive loop; <see cref="Closed"/> on whichever
/// background thread noticed (the receive loop, a failed write, the heartbeat).</para>
/// <para>Once receiving, each side sends a <c>ping</c> whenever it has been quiet for <see cref="HeartbeatEvery"/>, and
/// drops a peer it hasn't heard from in <see cref="DropAfterSilence"/>. A machine that went to sleep, or a network path
/// that died without a reset, otherwise leaves a link that looks open forever (W6). Pings never reach
/// <see cref="FrameReceived"/>.</para>
/// </summary>
internal sealed class RemoteLink : IDisposable
{
    public static readonly TimeSpan DefaultHeartbeat = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan DefaultDropAfter = TimeSpan.FromSeconds(60);

    /// <summary>How long this side stays quiet before it pings.</summary>
    public TimeSpan HeartbeatEvery { get; init; } = DefaultHeartbeat;

    /// <summary>How long the peer may stay silent before the link is dropped.</summary>
    public TimeSpan DropAfterSilence { get; init; } = DefaultDropAfter;

    private long _lastReceivedMs = Environment.TickCount64, _lastSentMs = Environment.TickCount64;
    private Timer? _heartbeat;

    /// <summary>The protocol version both ends announce; a mismatch is refused with a message.</summary>
    public const int Version = 1;

    /// <summary>The largest frame either side accepts: a prompt with a few images, base64-encoded.</summary>
    public const int MaxFrameBytes = 8 * 1024 * 1024;

    private readonly Stream _stream;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly byte[] _buffer = new byte[64 * 1024];
    private int _start, _end;
    private int _closed;

    public RemoteLink(Stream stream, string peerPin)
    {
        _stream = stream;
        PeerPin = peerPin;
    }

    /// <summary>The peer's pinned key (<see cref="DeviceTrust.PinOf"/>).</summary>
    public string PeerPin { get; }

    /// <summary>The peer's device name, from its hello.</summary>
    public string PeerName { get; internal set; } = "";

    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    public event Action<JsonObject>? FrameReceived;

    /// <summary>The connection ended; the argument says why when it wasn't a clean close.</summary>
    public event Action<string?>? Closed;

    public async Task SendAsync(JsonObject frame, CancellationToken ct = default)
    {
        var bytes = Encoding.UTF8.GetBytes(frame.ToJsonString() + "\n");
        if (bytes.Length > MaxFrameBytes) throw new RemoteLinkException("message too large to send");
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(bytes, ct).ConfigureAwait(false);
            await _stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>How much may wait to be written before the peer counts as stalled. A session's stream can outrun a slow
    /// network; past this the link closes rather than holding an ever-growing backlog in memory.</summary>
    public const long MaxQueuedBytes = 64L * 1024 * 1024;

    private readonly Channel<byte[]> _outbox = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private long _queuedBytes;
    private int _writerStarted;

    /// <summary>Queues a frame; frames go out in the order queued. False when the link is closed or the frame is over
    /// <see cref="MaxFrameBytes"/> (nothing is sent; the link stays up). A failed write, or a backlog past
    /// <see cref="MaxQueuedBytes"/>, closes the link.</summary>
    public bool TrySend(JsonObject frame)
    {
        if (IsClosed) return false;
        var bytes = Encoding.UTF8.GetBytes(frame.ToJsonString() + "\n");
        if (bytes.Length > MaxFrameBytes) return false;
        if (Interlocked.Add(ref _queuedBytes, bytes.Length) > MaxQueuedBytes)
        {
            Close("the other machine isn't keeping up");
            return false;
        }
        _outbox.Writer.TryWrite(bytes);
        if (Interlocked.Exchange(ref _writerStarted, 1) == 0) Task.Run(WriteLoop);
        return true;
    }

    /// <summary><see cref="TrySend"/>, for frames that can't be too large.</summary>
    public void Send(JsonObject frame) => TrySend(frame);

    private async Task WriteLoop()
    {
        try
        {
            await foreach (var bytes in _outbox.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                await _writeGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    await _stream.WriteAsync(bytes).ConfigureAwait(false);
                    await _stream.FlushAsync().ConfigureAwait(false);
                }
                finally
                {
                    _writeGate.Release();
                }
                Interlocked.Add(ref _queuedBytes, -bytes.Length);
                Volatile.Write(ref _lastSentMs, Environment.TickCount64);
            }
        }
        catch (Exception ex)
        {
            Close(IsClosed ? null : ex.Message);
        }
    }

    /// <summary>The next frame, or null when the peer closed cleanly between frames. Throws
    /// <see cref="InvalidDataException"/> for an oversized or malformed frame.</summary>
    public async Task<JsonObject?> ReadFrameAsync(CancellationToken ct = default)
    {
        using var line = new MemoryStream();
        while (true)
        {
            if (_start == _end)
            {
                _start = 0;
                _end = await _stream.ReadAsync(_buffer, ct).ConfigureAwait(false);
                if (_end == 0)
                {
                    if (line.Length == 0) return null;
                    throw new InvalidDataException("connection closed mid-message");
                }
            }
            int newline = Array.IndexOf(_buffer, (byte)'\n', _start, _end - _start);
            int take = (newline < 0 ? _end : newline) - _start;
            if (line.Length + take > MaxFrameBytes) throw new InvalidDataException("message too large");
            line.Write(_buffer, _start, take);
            _start += take;
            if (newline >= 0)
            {
                _start++;
                break;
            }
        }
        JsonNode? node;
        try { node = JsonNode.Parse(line.GetBuffer().AsSpan(0, (int)line.Length)); }
        catch (Exception ex) { throw new InvalidDataException("malformed message", ex); }
        if (node is not JsonObject frame || frame["t"]?.GetValueKind() != System.Text.Json.JsonValueKind.String)
            throw new InvalidDataException("message has no type");
        return frame;
    }

    /// <summary>Starts the receive loop (every frame to <see cref="FrameReceived"/> until the peer closes or misbehaves)
    /// and the heartbeat.</summary>
    public void StartReceiving()
    {
        Volatile.Write(ref _lastReceivedMs, Environment.TickCount64);
        _heartbeat = new Timer(_ => Beat(), null, HeartbeatEvery / 2, HeartbeatEvery / 2);
        Task.Run(ReceiveLoop);
    }

    // Checked twice per heartbeat period, so a ping goes out within half a period of falling quiet.
    private void Beat()
    {
        if (IsClosed) return;
        long now = Environment.TickCount64;
        if (now - Volatile.Read(ref _lastReceivedMs) > (long)DropAfterSilence.TotalMilliseconds)
        {
            Close($"no answer for {(int)DropAfterSilence.TotalSeconds} seconds");
            return;
        }
        if (now - Volatile.Read(ref _lastSentMs) >= (long)HeartbeatEvery.TotalMilliseconds) TrySend(Frame(PingType));
    }

    private const string PingType = "ping";

    private async Task ReceiveLoop()
    {
        string? reason = null;
        try
        {
            while (!IsClosed && await ReadFrameAsync().ConfigureAwait(false) is { } frame)
            {
                Volatile.Write(ref _lastReceivedMs, Environment.TickCount64);
                if (Str(frame, "t") != PingType) FrameReceived?.Invoke(frame);
            }
        }
        catch (Exception ex)
        {
            // A handler's own failure ends the link too, rather than leaving it open with nobody reading.
            reason = IsClosed ? null : ex.Message;
        }
        Close(reason);
    }

    public void Close(string? reason = null)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        _heartbeat?.Dispose();
        _outbox.Writer.TryComplete();
        try { _stream.Dispose(); } catch { /* already gone */ }
        Closed?.Invoke(reason);
    }

    public void Dispose() => Close();

    /// <summary>A string field of a peer's frame, or null when absent or not a string.</summary>
    public static string? Str(JsonObject? frame, string key) =>
        frame?[key] is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.String ? v.GetValue<string>() : null;

    /// <summary>A frame of type <paramref name="type"/>.</summary>
    public static JsonObject Frame(string type) => new() { ["t"] = type };

    /// <summary>A peer-supplied name made safe to show: no control characters, trimmed, at most 64 characters.</summary>
    public static string CleanName(string? name)
    {
        var s = new string((name ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (s.Length > 64) s = s[..64];
        return s.Length > 0 ? s : "unnamed machine";
    }
}

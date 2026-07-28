namespace Perch.Data;

using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Perch.Platform;

/// <summary>What a reachability check found, so settings can say which of the two opt-ins is missing rather
/// than just reporting that nothing works. See <see cref="TeamsCallController.ProbeAsync"/>.</summary>
public enum TeamsApiStatus
{
    /// <summary>No Teams client is running; there's nothing to connect to yet, and nothing to fix.</summary>
    NotRunning,

    /// <summary>Teams is running but isn't listening — its third-party app API setting is off.</summary>
    ApiDisabled,

    /// <summary>Teams is listening; the integration can connect.</summary>
    Reachable,
}

/// <summary>
/// The Microsoft Teams implementation of <see cref="ICallController"/>, over the local WebSocket API the
/// Teams client exposes on <c>127.0.0.1:8124</c> — the same channel Stream Deck's Teams buttons use. This
/// is the <em>only</em> product-specific piece of the microphone feature: detection, naming and
/// jump-to-window all work identically for Slack, Zoom or a browser without it, and everything here is
/// additive.
///
/// What it buys over the generic path is real in-app mute — the Teams UI and the other participants agree
/// with Perch, instead of the capture device being silenced behind Teams' back — plus authoritative meeting
/// state, which a microphone stream can never give: only Teams knows the difference between "in a meeting,
/// muted" and "not in a meeting".
///
/// <para><b>Not a Microsoft-documented API.</b> It is undocumented, unversioned beyond its
/// <c>protocol-version</c> query parameter, and unavailable in classic Teams. Everything here therefore
/// degrades quietly: any failure lands in <see cref="CallLinkState.Unavailable"/> and the UI falls back to
/// the generic affordances.</para>
///
/// <para><b>Requires two opt-ins.</b> The user must enable it in Teams (Settings → Privacy → Third-party
/// app API → Manage API → Enable API), and Teams shows an in-app authorisation prompt on the first
/// connection, handing back a token to persist. Because that prompt is intrusive, Perch must only ever
/// construct and <see cref="Start"/> this behind an explicit setting — and the retry schedule below is
/// deliberately slow when approval hasn't been granted, so a declined prompt is never spammed.</para>
///
/// Lives in <c>Perch.Core</c> rather than a platform project because it is pure sockets and JSON: the Teams
/// client exposes the same API on macOS, so this works on every head as-is.
/// </summary>
public sealed class TeamsCallController : ICallController
{
    private const int Port = 8124;

    // How long to wait before looking for Teams again when it isn't running at all — cheap check, no need
    // to be brisk about it.
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(10);

    // Reconnect backoff after a connection that had been working (or a refused connect, which is what a
    // disabled API looks like): 5s doubling to a minute.
    private static readonly TimeSpan FirstRetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(60);

    // The special case: we connected with no token and the session ended without Teams ever granting one,
    // i.e. the user ignored or dismissed the authorisation prompt. Each attempt pops that prompt again, so
    // back off hard rather than nagging.
    private static readonly TimeSpan UnapprovedRetryDelay = TimeSpan.FromMinutes(5);

    private readonly Func<string?> _readToken;
    private readonly Action<string?> _writeToken;
    private readonly object _gate = new();

    private CancellationTokenSource? _cts;
    private ClientWebSocket? _socket;
    private CallLinkState _state = CallLinkState.Disabled;
    private CallSnapshot? _current;
    private int _requestId;

    /// <summary>
    /// Creates the controller. The token accessors are injected rather than reaching for settings directly
    /// so this stays testable and storage-agnostic: <paramref name="readToken"/> supplies the persisted
    /// pairing token (null the first time), and <paramref name="writeToken"/> is called when Teams issues a
    /// new one — it should persist it, or the user gets the authorisation prompt again next launch.
    /// </summary>
    public TeamsCallController(Func<string?> readToken, Action<string?> writeToken)
    {
        _readToken = readToken;
        _writeToken = writeToken;
    }

    public CallLinkState State { get { lock (_gate) return _state; } }

    public CallSnapshot? Current { get { lock (_gate) return _current; } }

    public event Action? Changed;

    /// <summary>Whether a Teams client is running at all. Static so callers can cheaply decide whether the
    /// integration is worth mentioning before constructing anything.</summary>
    public static bool IsTeamsRunning()
    {
        foreach (var name in MicApps.TeamsProcessNames)
        {
            try
            {
                var found = Process.GetProcessesByName(name);
                foreach (var p in found) p.Dispose();
                if (found.Length > 0) return true;
            }
            catch { /* enumeration denied — assume not running */ }
        }
        return false;
    }

    /// <summary>
    /// Whether the local API can be reached, for telling the user what to fix rather than leaving the
    /// integration mysteriously inert. A plain TCP connect: the port is only open when the third-party app
    /// API is switched on inside Teams, so an open port is the one reliable signal that the setting is on.
    /// Deliberately no WebSocket handshake — that would consume Teams' single client slot and could pop the
    /// approval prompt, neither of which a status check should do.
    /// </summary>
    public static async Task<TeamsApiStatus> ProbeAsync(CancellationToken ct = default)
    {
        if (!IsTeamsRunning()) return TeamsApiStatus.NotRunning;
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            await client.ConnectAsync(System.Net.IPAddress.Loopback, Port, ct)
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(2), ct);
            return client.Connected ? TeamsApiStatus.Reachable : TeamsApiStatus.ApiDisabled;
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            // Refused or timed out: Teams is up but isn't listening, which in practice means the API setting
            // is off. (It can't distinguish that from a firewall on loopback, but the advice is the same.)
            return TeamsApiStatus.ApiDisabled;
        }
    }

    public void Start()
    {
        CancellationTokenSource cts;
        lock (_gate)
        {
            if (_cts is not null) return;
            _cts = cts = new CancellationTokenSource();
        }
        _ = RunAsync(cts.Token);
    }

    public void Stop()
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            cts = _cts;
            _cts = null;
        }
        if (cts is null) return;
        try { cts.Cancel(); } catch { }
        cts.Dispose();
        SetState(CallLinkState.Disabled);
        Publish(null);
    }

    public void Dispose() => Stop();

    // The connect / read / reconnect loop. Runs until Stop cancels it; every exit path from a session ends
    // in a delay so a missing or disabled Teams API costs one refused connection per interval.
    private async Task RunAsync(CancellationToken ct)
    {
        var retry = FirstRetryDelay;
        while (!ct.IsCancellationRequested)
        {
            if (!IsTeamsRunning())
            {
                SetState(CallLinkState.Unavailable);
                Publish(null);
                if (!await Wait(IdleDelay, ct)) return;
                continue;
            }

            var hadToken = !string.IsNullOrEmpty(Read());
            var approved = false;
            try
            {
                SetState(CallLinkState.Connecting);
                approved = await SessionAsync(ct);
                retry = FirstRetryDelay; // a session that actually ran resets the backoff
            }
            catch (OperationCanceledException) { break; }
            catch
            {
                // Refused (API disabled / Teams starting up), or the socket faulted. Nothing to report.
            }

            SetState(CallLinkState.Unavailable);
            Publish(null);

            var delay = !hadToken && !approved ? UnapprovedRetryDelay : retry;
            if (!await Wait(delay, ct)) return;
            retry = TimeSpan.FromTicks(Math.Min(retry.Ticks * 2, MaxRetryDelay.Ticks));
        }

        SetState(CallLinkState.Disabled);
        Publish(null);
    }

    // One connection, read until it closes. Returns whether Teams granted access during it — i.e. whether a
    // token was already valid or a fresh one arrived — which is what decides how long to wait before trying
    // again (see UnapprovedRetryDelay).
    private async Task<bool> SessionAsync(CancellationToken ct)
    {
        using var socket = new ClientWebSocket();
        var token = Read();

        // device/manufacturer/app are just labels Teams shows in its own API settings list; they are
        // deliberately not machine-identifying, and this never leaves the loopback interface anyway.
        var uri = new Uri(
            $"ws://127.0.0.1:{Port}/?protocol-version=2.0.0&manufacturer=Perch&device=Perch&app=Perch" +
            $"&app-version={Uri.EscapeDataString(AppInfo.Version)}&token={Uri.EscapeDataString(token ?? "")}");

        await socket.ConnectAsync(uri, ct);
        lock (_gate) _socket = socket;

        // With a stored token we're live immediately; without one, Teams is showing its approval prompt and
        // nothing can be driven until a tokenRefresh arrives.
        var approved = !string.IsNullOrEmpty(token);
        SetState(approved ? CallLinkState.Connected : CallLinkState.AwaitingApproval);

        try
        {
            var buffer = new byte[8192];
            var pending = new StringBuilder();
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                if (result.MessageType == WebSocketMessageType.Close) break;

                pending.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                if (!result.EndOfMessage) continue; // a frame-split message — keep accumulating

                var text = pending.ToString();
                pending.Clear();
                if (Handle(text)) approved = true;
            }
        }
        finally
        {
            lock (_gate) { if (ReferenceEquals(_socket, socket)) _socket = null; }
        }

        return approved;
    }

    // Parses one message. Returns true when this message proves Teams has granted access (a token refresh, an
    // acknowledgement, or any state update — Teams sends none of those to an unauthorised client).
    private bool Handle(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;

            var granted = false;

            // A plain "{"requestId":0,"response":"Success"}" ack. Teams sends one immediately on accepting a
            // connection, so it doubles as proof we're authorised — worth acting on, because a client that
            // waits for meeting state would sit in AwaitingApproval for as long as the user stays out of a call.
            if (root.TryGetProperty("response", out var response)
                && response.ValueKind == JsonValueKind.String
                && string.Equals(response.GetString(), "Success", StringComparison.OrdinalIgnoreCase))
            {
                granted = true;
                SetState(CallLinkState.Connected);
            }

            // The pairing handshake: Teams hands over a token once the user accepts the prompt. Persist it
            // so the prompt is a one-time cost. Teams may also rotate it mid-session.
            if (root.TryGetProperty("tokenRefresh", out var refreshed)
                && refreshed.ValueKind == JsonValueKind.String)
            {
                var value = refreshed.GetString();
                if (!string.IsNullOrEmpty(value))
                {
                    _writeToken(value);
                    granted = true;
                    SetState(CallLinkState.Connected);
                }
            }

            if (root.TryGetProperty("meetingUpdate", out var update)
                && update.ValueKind == JsonValueKind.Object)
            {
                Publish(Merge(Current, Child(update, "meetingState"), Child(update, "meetingPermissions")));
                granted = true;
                SetState(CallLinkState.Connected);
            }

            return granted;
        }
        catch
        {
            return false; // an unparseable frame is not worth dropping the connection over
        }
    }

    /// <summary>
    /// Folds one <c>meetingUpdate</c> into the snapshot we already had, keeping any field the frame doesn't
    /// mention.
    /// <para>
    /// <b>This merge is load-bearing.</b> Teams sends <em>partial</em> updates: a frame may carry only
    /// <c>meetingPermissions</c> (that is exactly what it sends on connecting outside a call) or only some
    /// of <c>meetingState</c>'s fields. Rebuilding the whole snapshot from each frame therefore silently
    /// resets everything the frame omitted — which showed up as Perch never noticing a mute made inside
    /// Teams, because the next permissions-only frame reset <c>IsMuted</c> to false.
    /// </para>
    /// Internal rather than private so the merge can be tested directly against captured frames — it is the
    /// part of this class worth pinning down, and the only part testable without a running Teams.
    /// </summary>
    internal static CallSnapshot Merge(CallSnapshot? previous, JsonElement state, JsonElement permissions)
    {
        previous ??= new CallSnapshot(IsInMeeting: false, IsMuted: false);

        var canToggleMute = Flag(permissions, "canToggleMute");
        var canLeave = Flag(permissions, "canLeave");

        // Being in a call: the state's own flag when present, else inferred from the permissions, because
        // Teams only grants leave/mute while there is a call to leave or mute. The inference matters in both
        // directions — it's what lets a permissions-only frame turn the strip on when a call starts, and, more
        // importantly, what stops a merged IsInMeeting from staying stuck at true after the call ends.
        var inMeeting = Flag(state, "isInMeeting")
            ?? (permissions.ValueKind == JsonValueKind.Object
                ? (canLeave ?? false) || (canToggleMute ?? false)
                : previous.IsInMeeting);

        return previous with
        {
            IsInMeeting = inMeeting,
            IsMuted = Flag(state, "isMuted") ?? previous.IsMuted,
            // Teams' own model calls this isVideoOn; accept isCameraOn too, in case a revision renames it.
            IsCameraOn = Flag(state, "isVideoOn") ?? Flag(state, "isCameraOn") ?? previous.IsCameraOn,
            IsHandRaised = Flag(state, "isHandRaised") ?? previous.IsHandRaised,
            IsRecording = Flag(state, "isRecordingOn") ?? previous.IsRecording,
            IsSharing = Flag(state, "isSharing") ?? previous.IsSharing,
            CanToggleMute = canToggleMute ?? previous.CanToggleMute,
            CanLeave = canLeave ?? previous.CanLeave,
        };
    }

    private static JsonElement Child(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object
        && parent.TryGetProperty(name, out var child)
        && child.ValueKind == JsonValueKind.Object ? child : default;

    // null means "this frame didn't mention the field", which is what the merge above needs to tell apart
    // from an explicit false.
    private static bool? Flag(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    public void ToggleMute() => Send("toggle-mute");

    public void LeaveCall() => Send("leave-call");

    // Fire-and-forget command. Silently ignored unless a live, authorised connection exists — the UI only
    // offers these buttons in that state, and a command that can't be delivered is better dropped than
    // queued for a connection that may never come back.
    private void Send(string action)
    {
        ClientWebSocket? socket;
        lock (_gate)
        {
            if (_state != CallLinkState.Connected) return;
            socket = _socket;
        }
        if (socket is null || socket.State != WebSocketState.Open) return;

        var id = Interlocked.Increment(ref _requestId);
        var payload = $"{{\"action\":\"{action}\",\"parameters\":{{}},\"requestId\":{id}}}";
        _ = SendAsync(socket, payload);
    }

    private static async Task SendAsync(ClientWebSocket socket, string payload)
    {
        try
        {
            await socket.SendAsync(
                new ArraySegment<byte>(Encoding.UTF8.GetBytes(payload)),
                WebSocketMessageType.Text,
                endOfMessage: true,
                CancellationToken.None);
        }
        catch { /* the socket died under us; the read loop will notice and reconnect */ }
    }

    private string? Read()
    {
        try { return _readToken(); }
        catch { return null; }
    }

    // Returns false when cancelled, so callers can exit the loop without catching.
    private static async Task<bool> Wait(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct);
            return true;
        }
        catch (OperationCanceledException) { return false; }
    }

    private void SetState(CallLinkState state)
    {
        lock (_gate)
        {
            if (_state == state) return;
            _state = state;
        }
        Changed?.Invoke();
    }

    private void Publish(CallSnapshot? snapshot)
    {
        lock (_gate)
        {
            if (_current == snapshot) return;
            _current = snapshot;
        }
        Changed?.Invoke();
    }
}

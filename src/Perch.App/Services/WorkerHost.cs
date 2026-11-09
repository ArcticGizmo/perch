using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Avalonia.Threading;
using Perch.Data;
using Perch.Platform;
using Perch.Remote;

namespace Perch.Avalonia.Services;

/// <summary>
/// This machine's part in remote workers (docs/remote-workers-plan.md): its device identity, the worker listener
/// (started and stopped with <see cref="AppSettings.WorkerEnabled"/>), the pairing window, the paired-machine list, and
/// the live sessions it offers connected controllers (<see cref="Offer"/>, served by a <see cref="WorkerSessionHub"/>).
/// <para>Raises <see cref="Changed"/> on the UI thread.</para>
/// </summary>
internal sealed class WorkerHost : IDisposable
{
    public static readonly TimeSpan PairingWindow = TimeSpan.FromMinutes(2);

    private readonly AppSettings _settings;
    private readonly IDeviceKeyStore _keys;
    private X509Certificate2? _identity;
    private bool _identityTried;
    private WorkerServer? _server;
    private (string? Bind, int Port)? _listeningOn;
    private readonly WorkerSessionHub _hub = new() { MaxImageBytes = PerchSession.MaxImageBytes };
    private readonly Dictionary<PerchSession, WorkerSessionAdapter> _offered = [];
    private List<WorkerSessionInfo> _lastOffered = [];
    private DispatcherTimer? _pairingTimer;

    public WorkerHost(AppSettings settings, IDeviceKeyStore keys)
    {
        _settings = settings;
        _keys = keys;
        _hub.ControllersChanged += () => Dispatcher.UIThread.Post(() => Changed?.Invoke());
        Workers = new WorkerLinks(Identity, () => DeviceName);
        Workers.Changed += () => Changed?.Invoke();
    }

    /// <summary>This machine's connections to the workers it controls.</summary>
    public WorkerLinks Workers { get; }

    // Connects to every paired worker and drops the removed ones. Pairing is the opt-in, so there's no separate switch.
    private void SyncWorkers()
    {
        if (IsSupported) Workers.Sync(_settings.PairedDevices ?? []);
    }

    /// <summary>Whether this head can take part at all (a device key it can use for TLS).</summary>
    public bool IsSupported => _keys.IsSupported;

    /// <summary>Something shown on the Workers page changed (status, pairing window, the paired list).</summary>
    public event Action? Changed;

    /// <summary>Asks the user on this machine whether a controller may pair (set by the app, which owns the windows).</summary>
    public Func<PairingRequest, Task<bool>>? AskUser { get; set; }

    public string DeviceName => DeviceTrust.DeviceName(_settings.DeviceName);

    /// <summary>This machine's key fingerprint, for the user to recognise; null when there's no identity.</summary>
    public string? Fingerprint => Identity() is { } id ? DeviceTrust.Fingerprint(DeviceTrust.PinOf(id)) : null;

    /// <summary>One line for the Workers page: off, listening, or why it isn't.</summary>
    public string Status { get; private set; } = "Off";

    public bool IsListening => _server?.IsListening == true;
    public bool PairingOpen => _server?.PairingOpen == true;
    public DateTime PairingClosesUtc { get; private set; }
    public int ConnectedControllers => _hub.ConnectedNames().Count;

    public IReadOnlyList<PairedDevice> Paired(PairedRole role) =>
        (_settings.PairedDevices ?? []).Where(d => d.Role == role).OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    private X509Certificate2? Identity()
    {
        if (!_identityTried)
        {
            _identityTried = true;
            _identity = _keys.LoadOrCreate();
        }
        return _identity;
    }

    /// <summary>Starts, restarts or stops the listener to match the settings. Idempotent.</summary>
    public void Apply()
    {
        SyncWorkers();
        bool want = IsSupported && _settings.WorkerEnabled;
        SessionJournal.RecordingEnabled = want;
        var target = (Bind: string.IsNullOrWhiteSpace(_settings.WorkerBindAddress) ? null : _settings.WorkerBindAddress!.Trim(),
                      Port: _settings.WorkerPort);
        if (want && IsListening && _listeningOn == target) return;
        StopServer();
        if (!want)
        {
            SetStatus(IsSupported ? "Off" : "Not available on this system yet");
            return;
        }
        if (Identity() is not { } identity)
        {
            SetStatus("Couldn't create this machine's key, so it can't listen");
            return;
        }
        if (target.Port is < 1 or > 65535)
        {
            SetStatus($"Port {target.Port} isn't valid");
            return;
        }
        IPAddress address;
        if (target.Bind is null) address = IPAddress.IPv6Any;
        else if (!IPAddress.TryParse(target.Bind, out address!))
        {
            SetStatus($"\"{target.Bind}\" isn't an IP address of this machine");
            return;
        }

        var server = new WorkerServer(identity, () => DeviceName, FindController)
        {
            ApprovePairing = ApprovePairing,
        };
        server.Paired += device => Dispatcher.UIThread.Post(() => Remember(device));
        server.ControllerConnected += _hub.Serve;
        try
        {
            server.Start(address, target.Port);
        }
        catch (SocketException ex)
        {
            server.Dispose();
            SetStatus($"Couldn't listen on port {target.Port}: {ex.Message}");
            return;
        }
        _server = server;
        _listeningOn = target;
        SetStatus(target.Bind is null ? $"Listening on port {target.Port}" : $"Listening on {target.Bind}:{target.Port}");
    }

    private void StopServer()
    {
        _server?.Dispose();
        _server = null;
        _listeningOn = null;
        ClosePairing();
        _hub.DisconnectAll();
    }

    private PairedDevice? FindController(string pin) =>
        (_settings.PairedDevices ?? []).FirstOrDefault(d => d.Role == PairedRole.Controller && d.Pin == pin);

    // ── Pairing ─────────────────────────────────────────────────────────────────

    /// <summary>Lets one new controller ask to pair for the next two minutes. False when not listening.</summary>
    public bool OpenPairing()
    {
        if (_server is not { IsListening: true } server) return false;
        server.OpenPairing(PairingWindow);
        PairingClosesUtc = DateTime.UtcNow + PairingWindow;
        _pairingTimer?.Stop();
        _pairingTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _pairingTimer.Tick += (_, _) =>
        {
            if (!PairingOpen) ClosePairing();
            Changed?.Invoke();
        };
        _pairingTimer.Start();
        Changed?.Invoke();
        return true;
    }

    public void ClosePairing()
    {
        _server?.ClosePairing();
        _pairingTimer?.Stop();
        _pairingTimer = null;
        Changed?.Invoke();
    }

    private async Task<bool> ApprovePairing(PairingRequest request)
    {
        if (AskUser is not { } ask) return false;
        return await Dispatcher.UIThread.InvokeAsync(() => ask(request));
    }

    /// <summary>Pairs with a worker that has pairing open, as its controller. <paramref name="showCode"/> gets the
    /// code to compare (off the UI thread). Saves it on success; throws <see cref="RemoteLinkException"/> otherwise.</summary>
    public async Task<PairedDevice> PairWithWorkerAsync(string host, int port, Action<string> showCode, CancellationToken ct)
    {
        if (Identity() is not { } identity) throw new RemoteLinkException("this machine has no key to pair with");
        var device = await WorkerClient.PairAsync(host, port, identity, DeviceName, showCode, ct);
        await Dispatcher.UIThread.InvokeAsync(() => Remember(device));
        return device;
    }

    // Saves a newly paired machine, replacing an older entry for the same key and role (a re-pair after a rename).
    private void Remember(PairedDevice device)
    {
        var list = _settings.PairedDevices ??= [];
        list.RemoveAll(d => d.Pin == device.Pin && d.Role == device.Role);
        list.Add(device);
        _settings.Save();
        SyncWorkers();
        Changed?.Invoke();
    }

    /// <summary>Forgets a paired machine. A controller loses its connection at once.</summary>
    public void Forget(PairedDevice device)
    {
        _settings.PairedDevices?.RemoveAll(d => d.Pin == device.Pin && d.Role == device.Role);
        if (_settings.PairedDevices is { Count: 0 }) _settings.PairedDevices = null;
        _settings.Save();
        if (device.Role == PairedRole.Controller) _hub.Disconnect(device.Pin);
        SyncWorkers();
        Changed?.Invoke();
    }

    /// <summary>Makes a new device key: every machine paired with this one has to pair again.</summary>
    public void ResetIdentity()
    {
        Workers.Sync([]);   // every link uses the old key
        StopServer();
        _keys.Reset();
        _identity?.Dispose();
        _identity = null;
        _identityTried = false;
        _settings.PairedDevices = null;
        _settings.Save();
        Apply();
        Changed?.Invoke();
    }

    // ── Offered sessions ────────────────────────────────────────────────────────

    /// <summary>Offers a live Perch session on this machine to connected controllers (UI thread), until it ends. Its
    /// title, id, model, mode and effort follow it. Sessions this machine drives on another one aren't offered.</summary>
    public void Offer(PerchSession session)
    {
        if (session.IsRemote || session.HasEnded || _offered.ContainsKey(session)) return;
        _offered[session] = new WorkerSessionAdapter(session);
        session.TitleChanged += Republish;
        session.Conversation.StateChanged += Republish;
        session.Restarted += _ => Republish();
        session.Ended += s =>
        {
            _offered.Remove(s);
            Republish();
        };
        Republish();
    }

    // Sends controllers the list when what they'd see changed. StateChanged fires at every turn boundary, so the list is
    // compared first.
    private void Republish()
    {
        var adapters = _offered.Values.ToList();
        var infos = adapters.Select(a => a.Info()).ToList();
        if (infos.SequenceEqual(_lastOffered)) return;
        _lastOffered = infos;
        _hub.SetSessions([.. infos.Zip(adapters, (i, a) => (i, (IWorkerSession)a))]);
    }

    private void SetStatus(string status)
    {
        Status = status;
        Changed?.Invoke();
    }

    public void Dispose()
    {
        Workers.Dispose();
        StopServer();
        _identity?.Dispose();
    }
}

using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Perch.Data;
using static Perch.Platform.Windows.CoreAudio;

namespace Perch.Platform.Windows;

/// <summary>
/// Windows <see cref="IMicrophoneMonitor"/>. Answers "who has the microphone" by joining the two
/// independent things Windows knows, because neither alone is enough:
///
/// <list type="bullet">
/// <item><b>The CapabilityAccessManager ConsentStore</b> (the registry ledger behind the Windows privacy
/// indicator in the tray) names the app: a package family name for a Store app, a full executable path
/// otherwise, with <c>LastUsedTimeStop == 0</c> meaning "still using it". This is the identity half — no
/// pid guessing, and it covers Teams, Slack, Zoom, a browser tab and OBS uniformly.</item>
/// <item><b>WASAPI capture sessions</b> give the live half: a session in the <c>Active</c> state with the
/// owning pid. This is what tells us audio is genuinely flowing, and it supplies the pid the overlay needs
/// to jump to the app's window.</item>
/// </list>
///
/// The two are unioned, so a gap in either still yields a sighting. Nothing here knows about any specific
/// application — identity strings go to <see cref="MicApps"/> to be named, and the caller decides whether
/// any product-specific integration applies.
///
/// <para><b>Polled, not evented, on purpose.</b> WASAPI can push session notifications, but only through a
/// managed COM callback object living on the right apartment, and the ConsentStore side would still need
/// <c>RegNotifyChangeKeyValue</c> on its own thread. A two-second poll off the threadpool answers a
/// question whose whole point is "am I in a call" — where two seconds is imperceptible — for a fraction of
/// the moving parts, and it publishes only when the snapshot actually changes so nothing repaints
/// needlessly.</para>
/// </summary>
public sealed class MicrophoneMonitor : IMicrophoneMonitor
{
    private const string ConsentSubKey =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly object _gate = new();

    // path -> version-info FileDescription. Resolved once per executable: it never changes while the app is
    // installed, and re-reading version info for every process on every tick would be pure waste.
    private readonly Dictionary<string, string?> _describeCache = new(StringComparer.OrdinalIgnoreCase);

    private Timer? _timer;
    private bool _started;

    public MicSnapshot? Current { get; private set; }

    public event Action? Changed;

    public void Start()
    {
        lock (_gate)
        {
            if (_started) return;
            _started = true;
            // Fire immediately so a strip switched on mid-call populates at once, then every PollInterval.
            _timer = new Timer(_ => Tick(), null, TimeSpan.Zero, PollInterval);
        }
    }

    public void Stop()
    {
        Timer? timer;
        lock (_gate)
        {
            if (!_started) return;
            _started = false;
            timer = _timer;
            _timer = null;
        }
        timer?.Dispose();
        Publish(null);
    }

    public void Dispose() => Stop();

    private void Tick()
    {
        try
        {
            lock (_gate) { if (!_started) return; }
            Publish(Build());
        }
        catch
        {
            // Best-effort: a torn registry read or an audio device disappearing mid-enumeration just skips
            // this tick. Never let a background poll take the app down.
        }
    }

    private void Publish(MicSnapshot? snapshot)
    {
        // MicSnapshot has value equality (including its Users list), so an unchanged picture costs nothing.
        if (snapshot == Current) return;
        Current = snapshot;
        Changed?.Invoke();
    }

    // ── Snapshot assembly ────────────────────────────────────────────────────

    private MicSnapshot Build()
    {
        var audio = ReadAudio();

        var holders = new List<Holder>();

        // The identity half: every app the privacy ledger currently records as using the mic.
        foreach (var entry in ReadConsentStore())
            holders.Add(new Holder(entry.Identity, 0, false, entry.Since));

        // The live half: attribute each active capture session to a holder, adding one when the ledger
        // didn't mention it (belt-and-braces — it should have).
        foreach (var pid in audio.ActivePids)
        {
            var path = ProcessPath(pid);
            var match = holders.FindIndex(h => MicApps.IdentityMatchesPath(h.Identity, path));
            if (match >= 0)
            {
                var h = holders[match];
                holders[match] = h with { ProcessId = h.ProcessId == 0 ? pid : h.ProcessId, IsStreaming = true };
            }
            else if (!string.IsNullOrEmpty(path))
            {
                holders.Add(new Holder(path, pid, true, null));
            }
        }

        var users = holders
            .OrderByDescending(h => h.IsStreaming)             // a live stream is the interesting one
            .ThenByDescending(h => h.Since ?? DateTimeOffset.MinValue)
            .Select(h => new MicUser(
                Identity: h.Identity,
                DisplayName: MicApps.DisplayName(h.Identity, Describe(h.Identity, h.ProcessId)),
                ProcessId: h.ProcessId,
                IsStreaming: h.IsStreaming,
                Since: h.Since))
            .ToList();

        return new MicSnapshot(users, audio.DeviceName);
    }

    private readonly record struct Holder(string Identity, int ProcessId, bool IsStreaming, DateTimeOffset? Since);

    // ── The privacy ledger (identity + "in use right now") ───────────────────

    // Walks HKCU then HKLM (services and system components record under the machine hive). Packaged apps are
    // direct subkeys named by package family name; everything else lives under NonPackaged with its path
    // '#'-mangled, which is un-mangled here so an identity is always either a PFN or a real path.
    private static List<(string Identity, DateTimeOffset? Since)> ReadConsentStore()
    {
        var result = new List<(string, DateTimeOffset?)>();
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var root = hive.OpenSubKey(ConsentSubKey);
                if (root is null) continue;
                Collect(root, packaged: true, result);
                using var nonPackaged = root.OpenSubKey("NonPackaged");
                if (nonPackaged is not null) Collect(nonPackaged, packaged: false, result);
            }
            catch { /* hive unreadable — the other half still counts */ }
        }
        return result;
    }

    private static void Collect(RegistryKey parent, bool packaged, List<(string, DateTimeOffset?)> into)
    {
        foreach (var name in parent.GetSubKeyNames())
        {
            if (packaged && name.Equals("NonPackaged", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                using var key = parent.OpenSubKey(name);
                if (key is null) continue;

                // Only an explicit zero stop-time means "in use". A missing value is *not* treated as in
                // use: apps that have never touched the mic have no timestamps at all, and the WASAPI half
                // of the union catches a live stream the ledger somehow hasn't stamped.
                if (key.GetValue("LastUsedTimeStop") is not long stop || stop != 0) continue;

                var since = key.GetValue("LastUsedTimeStart") is long start && start > 0
                    ? DateTimeOffset.FromFileTime(start)
                    : (DateTimeOffset?)null;

                into.Add((packaged ? name : name.Replace('#', '\\'), since));
            }
            catch { /* skip an unreadable entry */ }
        }
    }

    // ── WASAPI (live streams + the device's name) ─────────────────────────────

    private readonly record struct AudioState(
        List<int> ActivePids,
        string? DeviceName);

    private static AudioState ReadAudio()
    {
        var activePids = new List<int>();
        var devices = new List<(IMMDevice Device, string Id, string Name, bool HasActive)>();
        IMMDeviceEnumerator? enumerator = null;
        IMMDeviceCollection? collection = null;

        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            if (enumerator.EnumAudioEndpoints(EDataFlow.eCapture, DEVICE_STATE_ACTIVE, out collection) != 0)
                return new AudioState(activePids, null);

            collection.GetCount(out var deviceCount);
            for (var i = 0; i < deviceCount; i++)
            {
                if (collection.Item(i, out var device) != 0) continue;
                if (device.GetId(out var id) != 0) { Release(device); continue; }
                var pidsHere = ActiveSessionPids(device);
                activePids.AddRange(pidsHere);
                devices.Add((device, id, FriendlyName(device) ?? id, pidsHere.Count > 0));
            }

            // Name the device that's actually in use; fall back to the communications default so the tooltip can
            // still name a sensible endpoint when nothing is capturing.
            var chosen = devices.FirstOrDefault(d => d.HasActive);
            if (chosen.Device is null && enumerator.GetDefaultAudioEndpoint(
                    EDataFlow.eCapture, Role_Communications, out var fallback) == 0)
            {
                fallback.GetId(out var fid);
                chosen = (fallback, fid, FriendlyName(fallback) ?? fid, false);
                devices.Add(chosen);
            }

            if (chosen.Device is null) return new AudioState(activePids, null);
            return new AudioState(activePids, chosen.Name);
        }
        catch
        {
            return new AudioState(activePids, null);
        }
        finally
        {
            foreach (var d in devices) Release(d.Device);
            Release(collection);
            Release(enumerator);
        }
    }

    // The pids of every Active capture session on one endpoint. Inactive/Expired sessions are skipped: they
    // linger in the list long after a call ends, so counting them would report a call that finished hours ago.
    private static List<int> ActiveSessionPids(IMMDevice device)
    {
        var pids = new List<int>();
        object? managerObj = null;
        IAudioSessionEnumerator? sessions = null;
        try
        {
            var iid = typeof(IAudioSessionManager2).GUID;
            if (device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out managerObj) != 0
                || managerObj is not IAudioSessionManager2 manager) return pids;
            if (manager.GetSessionEnumerator(out sessions) != 0) return pids;

            sessions.GetCount(out var count);
            for (var i = 0; i < count; i++)
            {
                if (sessions.GetSession(i, out var control) != 0) continue;
                try
                {
                    if (control.GetState(out var state) == 0
                        && state == AudioSessionState.Active
                        && control.GetProcessId(out var pid) == 0
                        && pid != 0)
                        pids.Add((int)pid);
                }
                finally { Release(control); }
            }
        }
        catch { /* endpoint went away mid-enumeration */ }
        finally
        {
            Release(sessions);
            Release(managerObj);
        }
        return pids;
    }

    private static string? FriendlyName(IMMDevice device)
    {
        try
        {
            if (device.OpenPropertyStore(STGM_READ, out var store) != 0) return null;
            try
            {
                var key = FriendlyNameKey;
                if (store.GetValue(ref key, out var value) != 0 || value.pwszVal == IntPtr.Zero) return null;
                return Marshal.PtrToStringUni(value.pwszVal);
            }
            finally { Release(store); }
        }
        catch { return null; }
    }

    // ── Naming help ──────────────────────────────────────────────────────────

    // The app's own description ("Microsoft Teams") from its executable's version info — the best name
    // available, and better than anything derivable from a package family name. Needs a path, which comes
    // either from the live process or from a NonPackaged identity (already a path).
    private string? Describe(string identity, int pid)
    {
        var path = pid != 0 ? ProcessPath(pid) : null;
        if (string.IsNullOrEmpty(path) && identity.Contains('\\')) path = identity;
        if (string.IsNullOrEmpty(path)) return null;

        lock (_describeCache)
        {
            if (_describeCache.TryGetValue(path, out var cached)) return cached;
        }

        string? description = null;
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            description = info.FileDescription?.Trim();
            if (string.IsNullOrEmpty(description)) description = null;
        }
        catch { /* unreadable (ACLs on a WindowsApps path, file gone) — fall back to the name table */ }

        lock (_describeCache)
        {
            _describeCache[path] = description;
        }
        return description;
    }

    // QueryFullProcessImageName rather than Process.MainModule: the latter throws for processes of a
    // different bitness or elevation, and a capture session frequently belongs to a sandboxed helper.
    private static string? ProcessPath(int pid)
    {
        if (pid <= 0) return null;
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var buffer = new char[1024];
            var size = buffer.Length;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? new string(buffer, 0, size) : null;
        }
        finally { CloseHandle(handle); }
    }

    private const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, char[] buffer, ref int size);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}

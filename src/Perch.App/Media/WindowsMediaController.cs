using System.Runtime.Versioning;
using Perch.Platform;
using Windows.Foundation;
using Windows.Media.Control;

namespace Perch.Avalonia.Media;

/// <summary>
/// Windows implementation of <see cref="IMediaController"/> over the System Media Transport Controls — the
/// same global session the volume flyout uses to show and drive whatever's playing (Spotify, a browser
/// media tab, Groove, …). Built into Windows, so nothing ships with Perch to make it work.
///
/// Lives in the app head (not <c>Perch.Platform.Windows</c>) for the same reason the Action-Center toast
/// notifier does: the SMTC WinRT projection is only available on the app's <c>net10.0-windows10.0.19041.0</c>
/// target, and the platform project targets bare <c>net10.0-windows</c>. It's <c>&lt;Compile Remove&gt;</c>d
/// from the cross-platform head, which uses <see cref="NullMediaController"/> instead.
///
/// The API is event-driven: we subscribe to the session manager's <c>CurrentSessionChanged</c> and, on the
/// active session, its metadata/playback change events, re-reading a fresh <see cref="MediaSnapshot"/> and
/// raising <see cref="Changed"/> each time. Reads are async WinRT calls fired without blocking the caller;
/// commands are fire-and-forget. Everything is wrapped best-effort — a flaky media source never throws out.
/// </summary>
[SupportedOSPlatform("windows10.0.17763.0")]
internal sealed class WindowsMediaController : IMediaController
{
    private readonly object _gate = new();
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private bool _started;

    public MediaSnapshot? Current { get; private set; }
    public event Action? Changed;

    public void Start()
    {
        lock (_gate)
        {
            if (_started) return;
            _started = true;
        }
        _ = InitAsync();
    }

    private async Task InitAsync()
    {
        try
        {
            var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            lock (_gate)
            {
                if (!_started) return; // stopped before the async request came back
                _manager = manager;
                _manager.CurrentSessionChanged += OnCurrentSessionChanged;
            }
            HookCurrentSession();
        }
        catch { /* best-effort: no session manager, no strip */ }
    }

    private void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
        => HookCurrentSession();

    // Detach the old session's handlers, latch the new current session, attach its handlers, and read once.
    private void HookCurrentSession()
    {
        try
        {
            GlobalSystemMediaTransportControlsSession? next;
            lock (_gate)
            {
                if (!_started || _manager is null) return;
                next = _manager.GetCurrentSession();
                if (ReferenceEquals(next, _session)) { Refresh(); return; }

                if (_session is not null)
                {
                    _session.MediaPropertiesChanged -= OnSessionChanged;
                    _session.PlaybackInfoChanged -= OnSessionChanged;
                    _session.TimelinePropertiesChanged -= OnSessionChanged;
                }
                _session = next;
                if (_session is not null)
                {
                    _session.MediaPropertiesChanged += OnSessionChanged;
                    _session.PlaybackInfoChanged += OnSessionChanged;
                    _session.TimelinePropertiesChanged += OnSessionChanged;
                }
            }
            Refresh();
        }
        catch { /* best-effort */ }
    }

    private void OnSessionChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args) => Refresh();
    private void OnSessionChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args) => Refresh();
    private void OnSessionChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args) => Refresh();

    // Reads the active session's metadata + playback info into a fresh snapshot and publishes it. Media
    // properties are async (a WinRT round-trip); playback info is a synchronous read on the same session.
    private async void Refresh()
    {
        try
        {
            GlobalSystemMediaTransportControlsSession? session;
            lock (_gate) session = _session;

            if (session is null) { Publish(null); return; }

            var playback = session.GetPlaybackInfo();
            if (playback is null) { Publish(null); return; } // transient state — treat as "nothing playing"
            var props = await session.TryGetMediaPropertiesAsync();

            // The session can vanish or swap out between the two reads — bail if it's no longer current.
            lock (_gate) { if (!ReferenceEquals(session, _session)) return; }

            string title = props?.Title ?? "";
            if (string.IsNullOrWhiteSpace(title)) { Publish(null); return; }

            var controls = playback.Controls;
            var status = playback.PlaybackStatus;

            // Timeline (best-effort — many sources report none, and some report a stale 0/0). Normalise to
            // a from-zero (position, duration) and quantise the position to whole seconds so a chatty source
            // doesn't repaint the overlay faster than the bar visibly moves.
            var (position, duration) = ReadTimeline(session);

            var snapshot = new MediaSnapshot(
                Title: title.Trim(),
                Artist: (props?.Artist ?? "").Trim(),
                IsPlaying: status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                CanPlayPause: controls.IsPlayEnabled || controls.IsPauseEnabled || controls.IsPlayPauseToggleEnabled,
                CanNext: controls.IsNextEnabled,
                CanPrevious: controls.IsPreviousEnabled,
                Position: position,
                Duration: duration);
            Publish(snapshot);
        }
        catch { /* best-effort — a torn read just skips this update */ }
    }

    private static (TimeSpan position, TimeSpan duration) ReadTimeline(GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            var t = session.GetTimelineProperties();
            var duration = t.EndTime - t.StartTime;
            if (duration <= TimeSpan.Zero) return (TimeSpan.Zero, TimeSpan.Zero); // no usable timeline
            var position = t.Position - t.StartTime;
            if (position < TimeSpan.Zero) position = TimeSpan.Zero;
            if (position > duration) position = duration;
            return (TimeSpan.FromSeconds(Math.Floor(position.TotalSeconds)), duration);
        }
        catch { return (TimeSpan.Zero, TimeSpan.Zero); }
    }

    private void Publish(MediaSnapshot? snapshot)
    {
        // Skip a no-op update so a chatty source (position ticks arrive as PlaybackInfoChanged) doesn't
        // repaint the overlay every second when nothing visible actually changed.
        if (snapshot == Current) return;
        Current = snapshot;
        Changed?.Invoke();
    }

    public void TogglePlayPause() => Command(s => s.TryTogglePlayPauseAsync());
    public void Next()            => Command(s => s.TrySkipNextAsync());
    public void Previous()        => Command(s => s.TrySkipPreviousAsync());

    private void Command(Func<GlobalSystemMediaTransportControlsSession, IAsyncOperation<bool>> op)
    {
        GlobalSystemMediaTransportControlsSession? session;
        lock (_gate) session = _session;
        if (session is null) return;
        try { _ = op(session); } catch { /* the source rejected it — nothing to do */ }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (!_started) return;
            _started = false;
            if (_session is not null)
            {
                _session.MediaPropertiesChanged -= OnSessionChanged;
                _session.PlaybackInfoChanged -= OnSessionChanged;
                _session.TimelinePropertiesChanged -= OnSessionChanged;
                _session = null;
            }
            if (_manager is not null)
            {
                _manager.CurrentSessionChanged -= OnCurrentSessionChanged;
                _manager = null;
            }
        }
        Publish(null);
    }

    public void Dispose() => Stop();
}

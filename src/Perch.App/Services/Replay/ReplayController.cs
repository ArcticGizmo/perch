using Avalonia.Threading;
using Perch.Data.Replay;

namespace Perch.Avalonia.Services.Replay;

/// <summary>
/// Drives the scrub position <c>T</c> over a recording: play/pause at a chosen speed, seek, and
/// step-by. Each move projects the sandbox off the UI thread then forces a rescan on it — the
/// established <c>Task.Run</c> → <c>Dispatcher.UIThread.Post</c> pattern — so the real app reacts to the
/// new state immediately instead of waiting out the watcher debounce. UI-free so it's the single engine
/// the Phase 3 controller window binds its transport to.
/// </summary>
internal sealed class ReplayController : IDisposable
{
    private readonly Projector _projector;
    private readonly Action _reconcile;
    private readonly DispatcherTimer _timer;

    // Wall-clock stamp of the previous tick, from the monotonic OS counter — NOT DateTime.Now, which is
    // now the virtual replay clock and would make playback advance itself.
    private long _lastTickStamp;
    private bool _projecting;

    /// <summary>Playback rate multiplier (recording-time / wall-time). 1× = real time.</summary>
    public double Speed { get; set; } = 4.0;

    public long PositionMs { get; private set; }
    public long DurationMs { get; }
    public bool IsPlaying { get; private set; }

    /// <summary>Raised (on the UI thread) after a projection lands, so a controller window can move its
    /// scrub bar. Carries the new position.</summary>
    public event Action<long>? PositionChanged;

    public ReplayController(Projector projector, long durationMs, Action reconcile)
    {
        _projector = projector;
        _reconcile = reconcile;
        DurationMs = Math.Max(0, durationMs);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(66) }; // ~15 fps
        _timer.Tick += OnTick;
    }

    public void Play()
    {
        if (IsPlaying)
            return;
        if (PositionMs >= DurationMs)
            PositionMs = 0; // replay from the top when starting at the end
        _lastTickStamp = Environment.TickCount64;
        IsPlaying = true;
        _timer.Start();
    }

    public void Pause()
    {
        IsPlaying = false;
        _timer.Stop();
    }

    public void TogglePlay()
    {
        if (IsPlaying) Pause(); else Play();
    }

    /// <summary>Jumps to <paramref name="t"/> (clamped) and reprojects. Pauses first — an explicit seek
    /// is a deliberate move, not part of playback.</summary>
    public void Seek(long t)
    {
        Pause();
        PositionMs = Math.Clamp(t, 0, DurationMs);
        Apply();
    }

    public void Step(long deltaMs) => Seek(PositionMs + deltaMs);

    private void OnTick(object? sender, EventArgs e)
    {
        var now = Environment.TickCount64;
        var elapsed = now - _lastTickStamp;
        _lastTickStamp = now;

        PositionMs = Math.Min(DurationMs, PositionMs + (long)(elapsed * Speed));
        Apply();

        if (PositionMs >= DurationMs)
            Pause(); // reached the end — stop cleanly (the window can loop if it wants)
    }

    // Project the sandbox for the current position off the UI thread, then reconcile + notify back on it.
    // Skips if a projection is still in flight so a slow rebuild can't queue up behind the timer.
    private void Apply()
    {
        if (_projecting)
            return;
        _projecting = true;
        var target = PositionMs;
        System.Threading.Tasks.Task.Run(() => _projector.MaterialiseAt(target))
            .ContinueWith(_ => Dispatcher.UIThread.Post(() =>
            {
                _reconcile();
                PositionChanged?.Invoke(target);
                _projecting = false;
            }));
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Tick -= OnTick;
    }
}

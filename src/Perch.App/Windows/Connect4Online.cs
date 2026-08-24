using Avalonia.Threading;
using Perch.Social;

namespace Perch.Avalonia.Windows;

/// <summary>
/// Drives one online Connect 4 game: it refreshes the authoritative <see cref="GameState"/> from the backend
/// (on demand, via a live <see cref="ISocialClient.SubscribeGame"/> nudge, and on a short fallback poll), and
/// sends the local player's moves / resignation. Every backend call runs off the UI thread; results are
/// marshalled back to the dispatcher before the <see cref="Updated"/> / <see cref="Failed"/> events fire, so
/// the owner-drawn board can consume them directly. The server stays the source of truth — a move that the
/// server rejects surfaces as <see cref="Failed"/> and the board re-enables input.
/// </summary>
internal sealed class Connect4OnlineController : IDisposable
{
    // The realtime nudge is best-effort; this poll guarantees the opponent's move shows within a few seconds
    // even if the socket is blocked (the 60s feed cadence is far too slow for a live game).
    private static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(3);

    private readonly ISocialClient _social;
    private readonly Guid _gameId;
    private IDisposable? _subscription;
    private DispatcherTimer? _poll;
    private bool _disposed;

    /// <summary>Fired on the UI thread with fresh authoritative state (initial load, your move, opponent move).</summary>
    public event Action<GameState>? Updated;

    /// <summary>Fired on the UI thread when a call is rejected (e.g. "not your turn"); transient network blips
    /// are swallowed and left to the next poll.</summary>
    public event Action<string>? Failed;

    public Connect4OnlineController(ISocialClient social, Guid gameId)
    {
        _social = social;
        _gameId = gameId;
    }

    public void Start()
    {
        // A moves-channel insert just nudges us to re-read (the DB is authoritative).
        _subscription = _social.SubscribeGame(_gameId, () => _ = RefreshAsync());
        _poll = new DispatcherTimer { Interval = PollEvery };
        _poll.Tick += (_, _) => _ = RefreshAsync();
        _poll.Start();
        _ = RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        try
        {
            var state = await _social.GetGameAsync(_gameId);
            Post(() => Updated?.Invoke(state));
        }
        catch (SocialException ex) { Post(() => Failed?.Invoke(ex.Message)); }
        catch { /* transient — the poll / subscription will retry */ }
    }

    public async Task DropAsync(int column)
    {
        try
        {
            var state = await _social.DropAsync(_gameId, column);
            Post(() => Updated?.Invoke(state));
        }
        catch (SocialException ex) { Post(() => Failed?.Invoke(ex.Message)); }
        catch { Post(() => Failed?.Invoke("Couldn't reach the server — try again.")); }
    }

    public async Task ResignAsync()
    {
        try
        {
            var state = await _social.ResignGameAsync(_gameId);
            Post(() => Updated?.Invoke(state));
        }
        catch (SocialException ex) { Post(() => Failed?.Invoke(ex.Message)); }
        catch { Post(() => Failed?.Invoke("Couldn't reach the server — try again.")); }
    }

    // Marshal an event back to the UI thread, and drop it if we've been disposed mid-flight.
    private void Post(Action action)
    {
        if (_disposed) return;
        if (Dispatcher.UIThread.CheckAccess()) { if (!_disposed) action(); }
        else Dispatcher.UIThread.Post(() => { if (!_disposed) action(); });
    }

    public void Dispose()
    {
        _disposed = true;
        _subscription?.Dispose();
        _poll?.Stop();
    }
}

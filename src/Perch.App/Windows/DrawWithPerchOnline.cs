using Avalonia.Threading;
using Perch.Games;
using Perch.Social;

namespace Perch.Avalonia.Windows;

/// <summary>
/// Drives one online "Draw with Perch" game: it refreshes the authoritative <see cref="DrawGameState"/> from the
/// backend (on demand, via a live <see cref="ISocialClient.SubscribeDrawGame"/> nudge, and on a short fallback
/// poll), and sends the local player's drawings / guesses / resignation. Every backend call runs off the UI
/// thread; results are marshalled back to the dispatcher before <see cref="Updated"/> / <see cref="Failed"/> fire,
/// so the owner-drawn board consumes them directly. The server stays the source of truth.
/// </summary>
internal sealed class DrawOnlineController : IDisposable
{
    private static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(3);

    private readonly ISocialClient _social;
    private readonly Guid _gameId;
    private IDisposable? _subscription;
    private DispatcherTimer? _poll;
    private bool _disposed;

    /// <summary>Fired on the UI thread with fresh authoritative state. The flag is true when the state is the
    /// answer to one of the local player's own actions (submit/guess/give up/resign), false for a poll/live
    /// refresh — the board only lets the latter update the screen when something actually changed.</summary>
    public event Action<DrawGameState, bool>? Updated;

    /// <summary>Fired on the UI thread when a call is rejected; transient network blips are left to the next poll.</summary>
    public event Action<string>? Failed;

    public DrawOnlineController(ISocialClient social, Guid gameId)
    {
        _social = social;
        _gameId = gameId;
    }

    public void Start()
    {
        _subscription = _social.SubscribeDrawGame(_gameId, () => _ = RefreshAsync());
        _poll = new DispatcherTimer { Interval = PollEvery };
        _poll.Tick += (_, _) => _ = RefreshAsync();
        _poll.Start();
        _ = RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        try
        {
            var state = await _social.GetDrawGameAsync(_gameId);
            Post(() => Updated?.Invoke(state, false));
        }
        catch (SocialException ex) { Post(() => Failed?.Invoke(ex.Message)); }
        catch { /* transient — the poll / subscription will retry */ }
    }

    public async Task SubmitRoundAsync(DrawDifficulty diff, string word, string hint, IReadOnlyList<DrawStroke> strokes)
    {
        try
        {
            var state = await _social.SubmitDrawRoundAsync(_gameId, diff, word, hint, strokes);
            Post(() => Updated?.Invoke(state, true));
        }
        catch (SocialException ex) { Post(() => Failed?.Invoke(ex.Message)); }
        catch { Post(() => Failed?.Invoke("Couldn't reach the server — try again.")); }
    }

    public async Task SubmitGuessAsync(Guid roundId, string guess)
    {
        try
        {
            var state = await _social.SubmitDrawGuessAsync(roundId, guess);
            Post(() => Updated?.Invoke(state, true));
        }
        catch (SocialException ex) { Post(() => Failed?.Invoke(ex.Message)); }
        catch { Post(() => Failed?.Invoke("Couldn't reach the server — try again.")); }
    }

    public async Task GiveUpAsync(Guid roundId)
    {
        try
        {
            var state = await _social.GiveUpDrawRoundAsync(roundId);
            Post(() => Updated?.Invoke(state, true));
        }
        catch (SocialException ex) { Post(() => Failed?.Invoke(ex.Message)); }
        catch { Post(() => Failed?.Invoke("Couldn't reach the server — try again.")); }
    }

    /// <summary>Resigns; true once the server has recorded it (the caller closes the board).</summary>
    public async Task<bool> ResignAsync()
    {
        try
        {
            var state = await _social.ResignDrawGameAsync(_gameId);
            Post(() => Updated?.Invoke(state, true));
            return true;
        }
        catch (SocialException ex) { Post(() => Failed?.Invoke(ex.Message)); }
        catch { Post(() => Failed?.Invoke("Couldn't reach the server — try again.")); }
        return false;
    }

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

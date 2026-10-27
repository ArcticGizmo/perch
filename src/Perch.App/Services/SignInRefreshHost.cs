using Avalonia.Threading;
using Perch.Data;
using Perch.Platform;

namespace Perch.Avalonia.Services;

/// <summary>
/// Runs the opt-in <see cref="SignInRefresher"/> over every known config dir once a minute while
/// <see cref="AppSettings.AutoRefreshSignIn"/> is on. A minute is fine-grained enough to land inside the
/// five-minute refresh window and coarse enough that a pass is nearly always a few file reads; the claude CLI only runs
/// for a dir that's actually due. The passes run off the UI thread, sequentially; results are applied back on it.
///
/// <para>The host keeps which accounts need a login (by real root, with the reason) for the overlay's "Login required"
/// button, raises <see cref="LoginLost"/> once when an account first needs one (the toast), and <see cref="Refreshed"/>
/// after any renewal or recovered login so the usage strip can re-poll with the new token. A failed renewal is retried after
/// <see cref="RetryAfterFailure"/> rather than every tick. Outcomes go to <c>logs/signin.log</c>, never a token.</para>
/// </summary>
internal sealed class SignInRefreshHost : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(2);
    // A renewal takes about a second, so its confirmation lingers long enough to be seen.
    private static readonly TimeSpan RenewedShownFor = TimeSpan.FromSeconds(10);
    private const string Log = "signin.log";

    private readonly SignInRefresher _refresher = new();
    private readonly IClaudeCredentials _primary;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<string, DateTime> _retryAt = new(ClaudeConfigDir.PathComparer);
    private Dictionary<string, string> _loginRequired = new(ClaudeConfigDir.PathComparer);
    private readonly Dictionary<string, SignInActivity> _activity = new(ClaudeConfigDir.PathComparer);
    private bool _started;
    private bool _running;

    /// <summary>Accounts whose login the refresher found dead, by <see cref="ClaudeConfigDir.RealRoot"/>, with why.</summary>
    public IReadOnlyDictionary<string, string> LoginRequired => _loginRequired;

    /// <summary>Raised on the UI thread when <see cref="LoginRequired"/> changes.</summary>
    public event Action<IReadOnlyDictionary<string, string>>? Changed;

    /// <summary>Raised on the UI thread the first time an account needs a login, with the reason.</summary>
    public event Action<ClaudeConfigDir, string>? LoginLost;

    /// <summary>Raised on the UI thread after a pass renewed at least one sign-in, or found a lost login back.</summary>
    public event Action? Refreshed;

    /// <summary>Raised on the UI thread when an account starts renewing, finishes, or its "renewed" confirmation lapses —
    /// with every account's current activity, by <see cref="ClaudeConfigDir.RealRoot"/>.</summary>
    public event Action<IReadOnlyDictionary<string, SignInActivity>>? ActivityChanged;

    public SignInRefreshHost(IClaudeCredentials primary)
    {
        _primary = primary;
        _timer = new DispatcherTimer { Interval = Interval };
        _timer.Tick += (_, _) => _ = Tick();
    }

    /// <summary>Starts or stops refreshing to match the setting. Idempotent. Call on the UI thread.</summary>
    public void SetEnabled(bool enabled)
    {
        if (enabled == _started) return;
        _started = enabled;
        DiagnosticLog.Append(Log, enabled ? "sign-in refresh on" : "sign-in refresh off");
        if (enabled)
        {
            _timer.Start();
            _ = Tick();
            return;
        }
        _timer.Stop();
        _retryAt.Clear();
        if (_activity.Count > 0)
        {
            _activity.Clear();
            ActivityChanged?.Invoke(new Dictionary<string, SignInActivity>(_activity, ClaudeConfigDir.PathComparer));
        }
        if (_loginRequired.Count > 0)
        {
            _loginRequired = new(ClaudeConfigDir.PathComparer);
            Changed?.Invoke(_loginRequired);
        }
    }

    /// <summary>Runs a pass now — after a login terminal is opened, so the button clears soon after the login lands.</summary>
    public void Nudge()
    {
        if (_started) _ = Tick();
    }

    private async Task Tick()
    {
        if (_running || !_started) return;
        _running = true;
        try
        {
            var now = DateTime.UtcNow;
            var due = ClaudeConfigSet.Instance.All
                .Where(d => !_retryAt.TryGetValue(d.RealRoot, out var at) || at <= now)
                .ToList();
            var results = await Task.Run(async () =>
            {
                var list = new List<(ClaudeConfigDir Dir, RefreshResult Result)>(due.Count);
                foreach (var dir in due)
                {
                    string root = dir.RealRoot;
                    void Renewing() => Dispatcher.UIThread.Post(() => { if (_started) SetActivity(root, SignInActivity.Renewing); });
                    list.Add((dir, await _refresher.RefreshIfDueAsync(SignInStore.For(dir, _primary), Clock.UtcNow, Renewing)));
                }
                return list;
            });
            if (_started) Apply(results);
        }
        finally
        {
            _running = false;
        }
    }

    private void Apply(List<(ClaudeConfigDir Dir, RefreshResult Result)> results)
    {
        // Dirs skipped for a retry back-off keep whatever they had.
        var next = new Dictionary<string, string>(_loginRequired, ClaudeConfigDir.PathComparer);
        bool refreshed = false;
        foreach (var (dir, r) in results)
        {
            string root = dir.RealRoot;
            if (r.Outcome == RefreshOutcome.Refreshed)
            {
                SetActivity(root, SignInActivity.Renewed);
                DispatcherTimer.RunOnce(() => ClearRenewed(root), RenewedShownFor);
            }
            else if (_activity.ContainsKey(root))
            {
                SetActivity(root, null);
            }

            if (r.Outcome == RefreshOutcome.LoginRequired)
            {
                next[root] = r.Reason ?? "The sign-in can't be renewed.";
                continue;
            }
            next.Remove(root);
            switch (r.Outcome)
            {
                case RefreshOutcome.Refreshed:
                    refreshed = true;
                    _retryAt.Remove(root);
                    DiagnosticLog.Append(Log, $"{dir.Label}: renewed");
                    break;
                case RefreshOutcome.Failed:
                    _retryAt[root] = DateTime.UtcNow + RetryAfterFailure;
                    DiagnosticLog.Append(Log, $"{dir.Label}: renewal failed, retrying in {RetryAfterFailure.TotalMinutes:0} min - {r.Reason}");
                    break;
            }
        }

        var lost = new HashSet<string>(next.Keys.Where(k => !_loginRequired.ContainsKey(k)), ClaudeConfigDir.PathComparer);
        bool recovered = _loginRequired.Keys.Any(k => !next.ContainsKey(k));
        bool changed = lost.Count > 0 || recovered;
        _loginRequired = next;
        if (changed) Changed?.Invoke(_loginRequired);

        foreach (var (dir, r) in results)
        {
            if (r.Outcome != RefreshOutcome.LoginRequired || !lost.Contains(dir.RealRoot)) continue;
            DiagnosticLog.Append(Log, $"{dir.Label}: login required - {r.Reason}");
            LoginLost?.Invoke(dir, next[dir.RealRoot]);
        }
        if (refreshed || recovered) Refreshed?.Invoke();
    }

    private void SetActivity(string root, SignInActivity? activity)
    {
        if (activity is { } a) _activity[root] = a;
        else if (!_activity.Remove(root)) return;
        ActivityChanged?.Invoke(new Dictionary<string, SignInActivity>(_activity, ClaudeConfigDir.PathComparer));
    }

    // Only the confirmation lapses on its timer; a renewal that started again since keeps its "renewing".
    private void ClearRenewed(string root)
    {
        if (_activity.TryGetValue(root, out var a) && a == SignInActivity.Renewed) SetActivity(root, null);
    }

    public void Dispose()
    {
        _started = false;
        _timer.Stop();
    }
}

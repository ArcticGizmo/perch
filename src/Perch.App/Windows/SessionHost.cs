using Avalonia.Threading;
using Iciclecreek.Terminal;
using Perch.Data;

namespace Perch.Avalonia.Windows;

/// <summary>
/// The hub for one Perch-controlled ConPTY <c>claude</c> session (session-control — the multi-surface
/// PoC, see <c>docs/session-control-poc.md</c> §5/§7). A single running <c>claude</c> is one process with
/// one PTY, so the model is <b>one owner, many attached surfaces</b>: this host owns the session and every
/// surface (the terminal window that renders the TUI, a terminal-free rich UI window, …) is a client of
/// it. <see cref="SendText"/> is the one write path every surface funnels through; <see cref="TranscriptPath"/>
/// is what every reader surface tails. Surfaces come and go; the session lives on the host.
///
/// <para><b>Which session is active can change under us.</b> We launch with a <em>known</em> id — a fresh
/// <c>Guid</c> passed as <c>--session-id</c>, or the id we <c>--resume</c>d — so the initial attach needs
/// no guessing. But a <c>/resume</c> or <c>/clear</c> typed into the TUI switches the PTY to a
/// <em>different</em> transcript, and <c>/rename</c> re-titles the current one. So we poll the cwd's
/// project folder for the newest <c>.jsonl</c> <em>written since launch</em>: when that file changes we
/// re-point readers (<see cref="ActiveSessionChanged"/>); when only its <c>/rename</c> title changes we
/// raise <see cref="TitleChanged"/>. The "since launch" filter is what stops an untouched older session
/// in the same folder from being mistaken for the live one — the fragility a bare mtime scan would have.
/// Best-effort throughout; the poll never throws.</para>
/// </summary>
internal sealed class SessionHost
{
    private readonly TerminalControl _terminal;
    private readonly string _cwd;
    private readonly string _projectDir;
    private readonly bool _resume;
    private readonly DateTime _launchedUtc;

    private DispatcherTimer? _poll;
    private string? _activePath;   // the .jsonl the PTY is currently appending to

    /// <param name="launchId">The id claude was told to use — a fresh GUID (<c>--session-id</c>) for a new
    /// session, or the resumed id (<c>--resume</c>). Never empty.</param>
    /// <param name="isResume">True when <paramref name="launchId"/> is a resumed (already-existing) session,
    /// so its transcript can be shown immediately rather than waited for.</param>
    public SessionHost(TerminalControl terminal, string cwd, string launchId, bool isResume)
    {
        _terminal = terminal;
        _cwd = cwd;
        _resume = isResume;
        _launchedUtc = DateTime.UtcNow;
        _projectDir = System.IO.Path.Combine(ClaudePaths.ProjectsDir, TranscriptLocator.EncodeProjectDir(cwd));

        SessionId = launchId;
        TranscriptPath = System.IO.Path.Combine(_projectDir, launchId + ".jsonl");
    }

    public string Cwd => _cwd;
    public string? SessionId { get; private set; }
    public string? TranscriptPath { get; private set; }
    public string? Title { get; private set; }
    public bool IsLive => _terminal.IsLive;

    /// <summary>Raised (on the UI thread) when the active session's transcript changes: the initial attach,
    /// or a <c>/resume</c> / <c>/clear</c> that switched the PTY to a different session.</summary>
    public event Action? ActiveSessionChanged;

    /// <summary>Raised (on the UI thread) when the <c>/rename</c> title of the current session changes.</summary>
    public event Action? TitleChanged;

    /// <summary>Injects a prompt into the shared PTY exactly as typing it and pressing Enter would — the
    /// one write path every input surface funnels through.</summary>
    public void SendText(string text)
    {
        var t = text?.Trim();
        if (string.IsNullOrEmpty(t) || !_terminal.IsLive) return;
        _ = _terminal.SendInputAsync(t + "\r", default);
    }

    /// <summary>Starts following the session (called once by the PTY-owning window after launch). A resumed
    /// transcript exists already, so it's announced immediately; a new one is announced once claude has
    /// created it (first poll tick).</summary>
    public void Begin()
    {
        if (_resume && TranscriptPath is { } p && File.Exists(p))
        {
            _activePath = p;
            ReadTitle();
            ActiveSessionChanged?.Invoke();
        }

        _poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(750) };
        _poll.Tick += (_, _) => Poll();
        _poll.Start();
    }

    private void Poll()
    {
        if (!_terminal.IsLive) return;
        try
        {
            if (!Directory.Exists(_projectDir)) return;

            // The newest transcript written since we launched: our own session once claude writes it, or a
            // /resume / /clear target (resuming bumps its mtime to now). An untouched older session keeps an
            // old mtime and is excluded — so it can never be mistaken for the live one.
            var newest = new DirectoryInfo(_projectDir).EnumerateFiles("*.jsonl")
                .Where(f => f.Length > 0 && f.LastWriteTimeUtc >= _launchedUtc.AddSeconds(-2))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();

            if (newest is not null && !PathEquals(newest.FullName, _activePath))
            {
                _activePath = newest.FullName;
                SessionId = Path.GetFileNameWithoutExtension(newest.Name);
                TranscriptPath = newest.FullName;
                ReadTitle();
                ActiveSessionChanged?.Invoke();
                return;
            }

            // Same transcript still active — surface a /rename title change on it.
            if (_activePath is not null)
            {
                var t = TranscriptReader.ReadTitle(_activePath);
                if (!string.Equals(t, Title, StringComparison.Ordinal))
                {
                    Title = t;
                    TitleChanged?.Invoke();
                }
            }
        }
        catch
        {
            // Folder briefly unreadable mid-write — try again on the next tick.
        }
    }

    private void ReadTitle()
    {
        try { Title = _activePath is null ? null : TranscriptReader.ReadTitle(_activePath); }
        catch { Title = null; }
    }

    private static bool PathEquals(string a, string? b) =>
        b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    public void Stop()
    {
        try { _poll?.Stop(); } catch { }
        _poll = null;
    }
}

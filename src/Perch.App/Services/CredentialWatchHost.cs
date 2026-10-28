using Avalonia.Threading;
using Perch.Data;

namespace Perch.Avalonia.Services;

/// <summary>
/// Watches the <c>.credentials.json</c> of each config dir that needs a login, so the "Login required" button clears as
/// soon as a login lands — through the button's terminal, <c>/login</c> in any Claude Code session, or anything else
/// that writes the file — rather than on the next refresher pass (a minute) or usage poll (five). Only those dirs are
/// watched, and only while they need it, so this is a handful of watchers that exist only while something is wrong.
/// A sign-in held in the Keychain (the macOS primary) has no file to watch and still clears on the next pass.
/// </summary>
internal sealed class CredentialWatchHost : IDisposable
{
    // Claude Code rewrites the file in more than one step; settle so one login is one re-check.
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(750);

    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(ClaudeConfigDir.PathComparer);
    private readonly DispatcherTimer _settle;

    /// <summary>Raised on the UI thread once a watched credentials file has changed and gone quiet.</summary>
    public event Action? Changed;

    public CredentialWatchHost()
    {
        _settle = new DispatcherTimer { Interval = Settle };
        _settle.Tick += (_, _) =>
        {
            _settle.Stop();
            Changed?.Invoke();
        };
    }

    /// <summary>Watches exactly <paramref name="dirs"/>' credentials files, dropping the rest. Call on the UI thread.</summary>
    public void Watch(IEnumerable<ClaudeConfigDir> dirs)
    {
        var wanted = new Dictionary<string, ClaudeConfigDir>(ClaudeConfigDir.PathComparer);
        foreach (var d in dirs) wanted.TryAdd(d.RealRoot, d);

        foreach (var root in _watchers.Keys.Where(r => !wanted.ContainsKey(r)).ToList())
            Drop(root);
        foreach (var (root, dir) in wanted)
            if (!_watchers.ContainsKey(root))
                Add(root, dir);
    }

    private void Add(string root, ClaudeConfigDir dir)
    {
        try
        {
            var file = dir.CredentialsFile;
            var watcher = new FileSystemWatcher(Path.GetDirectoryName(file)!, Path.GetFileName(file))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            };
            // These fire on a watcher thread; hop to the UI thread before touching the settle timer.
            watcher.Changed += OnFileEvent;
            watcher.Created += OnFileEvent;
            watcher.Renamed += OnFileEvent;
            // A watcher that dies (its folder removed) is dropped; the next Watch re-adds it if still wanted.
            watcher.Error += (_, _) => Dispatcher.UIThread.Post(() => Drop(root));
            watcher.EnableRaisingEvents = true;
            _watchers[root] = watcher;
        }
        catch
        {
            // Folder missing or unwatchable: the refresher's pass and the usage poll still catch the login.
        }
    }

    private void OnFileEvent(object? sender, FileSystemEventArgs e) =>
        Dispatcher.UIThread.Post(() =>
        {
            _settle.Stop();
            _settle.Start();
        });

    private void Drop(string root)
    {
        if (!_watchers.Remove(root, out var w)) return;
        try { w.EnableRaisingEvents = false; w.Dispose(); } catch { }
    }

    public void Dispose()
    {
        _settle.Stop();
        foreach (var root in _watchers.Keys.ToList()) Drop(root);
    }
}

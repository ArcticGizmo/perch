using Avalonia.Threading;
using Perch.Avalonia.Views;
using Perch.Data;
using Perch.Data.Work;

namespace Perch.Avalonia.Services;

/// <summary>
/// The tray's side of work items (docs/work-items-plan.md): it owns the <see cref="WorkItemStore"/>, keeps the loaded
/// list current (a watcher on the <c>work/</c> folder, so a <c>perch work …</c> call or a hand edit shows up at once),
/// projects it for the overlay, and records what Perch can see happen to member sessions: a title/folder snapshot
/// while they're live (so an ended one can still be named and resumed), and an "ended" line in the log when one
/// leaves the roster.
///
/// <para>Reads and writes go through the store's read-modify-write of one small file. User actions (a tick in a menu,
/// the window) run inline because they're a single tiny write the user is waiting on; anything the scan triggers runs
/// off the UI thread. <see cref="Changed"/> is raised on the UI thread.</para>
/// </summary>
internal sealed class WorkItemsHost : IDisposable
{
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(150);

    private readonly DispatcherTimer _settle;
    private FileSystemWatcher? _watcher;
    private bool _loading, _reloadAgain, _disposed;

    // Member sessions seen on the roster at the last scan, so one leaving it can be logged as ended once.
    private HashSet<string> _liveMembers = new(StringComparer.Ordinal);
    private bool _sawFirstScan;

    public WorkItemStore Store { get; }

    /// <summary>Every item, oldest first, as last loaded.</summary>
    public IReadOnlyList<WorkItem> Items { get; private set; } = [];

    /// <summary>The item list changed (a load finished, or an action here wrote). UI thread.</summary>
    public event Action? Changed;

    public WorkItemsHost(WorkItemStore store)
    {
        Store = store;
        _settle = new DispatcherTimer { Interval = Settle };
        _settle.Tick += (_, _) => { _settle.Stop(); Reload(); };
    }

    /// <summary>Takes a first load and starts watching the folder (creating it, so the watcher has something to
    /// attach to). Call on the UI thread.</summary>
    public void Start()
    {
        try { Directory.CreateDirectory(Store.Dir); } catch { /* the watcher just won't attach */ }
        EnsureWatcher();
        Reload();
    }

    public WorkItem? Find(string id) => Items.FirstOrDefault(i => i.Id == id);

    /// <summary>Loads the list synchronously, without watching (the headless render).</summary>
    internal void LoadNow() => Items = Store.LoadAll();

    /// <summary>The items this session belongs to.</summary>
    public IEnumerable<WorkItem> ItemsFor(string sessionId) => Items.Where(i => i.HasMember(sessionId));

    // ── Actions (UI thread, inline) ──

    public WorkItem? Create(string name)
    {
        var item = Store.Create(name);
        if (item is not null) Apply(item);
        return item;
    }

    /// <summary>Changes one item (read fresh, changed, written) and updates the list at once.</summary>
    public WorkItem? Mutate(string id, Func<WorkItem, bool> change)
    {
        var item = Store.Mutate(id, change);
        if (item is not null) Apply(item);
        return item;
    }

    public WorkItem? Mutate(string id, Action<WorkItem> change) => Mutate(id, i => { change(i); return true; });

    /// <summary>Puts a session in an item and logs it ("joined (manual)"). False when it was already there.</summary>
    public bool AddSession(string itemId, string sessionId, string? title, string? cwd, WorkJoin via)
    {
        bool added = false;
        Mutate(itemId, i =>
        {
            added = i.AddMember(sessionId, via, title, cwd);
            if (added) i.AddLog($"{Label(title, sessionId)} joined ({via.ToString().ToLowerInvariant()})", byPerch: true);
            return added;
        });
        if (added) _liveMembers.Add(sessionId);
        return added;
    }

    /// <summary>Takes a session out of an item and logs it.</summary>
    public bool RemoveSession(string itemId, string sessionId)
    {
        bool removed = false;
        Mutate(itemId, i =>
        {
            var title = i.Members.FirstOrDefault(m => m.SessionId == sessionId)?.Title;
            removed = i.RemoveMember(sessionId);
            if (removed) i.AddLog($"{Label(title, sessionId)} taken out", byPerch: true);
            return removed;
        });
        return removed;
    }

    /// <summary>Moves an item to a status and logs the change. Only the user calls this (D3).</summary>
    public WorkItem? SetStatus(string itemId, WorkItemStatus status) => Mutate(itemId, i =>
    {
        if (i.Status == status) return false;
        i.Status = status;
        i.AddLog(status switch
        {
            WorkItemStatus.Done => "Marked done",
            WorkItemStatus.Paused => "Paused",
            _ => i.Log.Count == 0 ? "Active" : "Resumed",
        }, byPerch: true);
        return true;
    });

    public WorkItem? AddNote(string itemId, string text) => Mutate(itemId, i =>
    {
        int before = i.Log.Count;
        i.AddLog(text, byPerch: false);
        return i.Log.Count > before;
    });

    public bool Delete(string itemId)
    {
        if (!Store.Delete(itemId)) return false;
        Items = Items.Where(i => i.Id != itemId).ToList();
        Changed?.Invoke();
        return true;
    }

    // Swaps a freshly written item into the list and tells listeners.
    private void Apply(WorkItem item)
    {
        var list = Items.ToList();
        int at = list.FindIndex(i => i.Id == item.Id);
        if (at >= 0) list[at] = item; else list.Add(item);
        Items = list;
        Changed?.Invoke();
    }

    // ── The scan ──

    /// <summary>
    /// Called with every roster scan (plus not-running Perch rows). Refreshes the title/folder snapshot of each live
    /// member, and logs "ended" for a member that was on the roster at the last scan and isn't now. Writes, if any,
    /// happen off the UI thread; nothing is written when nothing changed.
    /// </summary>
    public void ObserveSessions(IReadOnlyList<ClaudeSession> roster)
    {
        if (Items.Count == 0) return;
        var byId = new Dictionary<string, ClaudeSession>(StringComparer.Ordinal);
        foreach (var s in roster)
            if (!s.IsDormant) byId.TryAdd(s.SessionId, s);

        var refresh = new List<(string Item, string Session, string Title, string Cwd)>();
        var ended = new List<(string Item, string Session, string? Title)>();
        var live = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in Items)
        {
            if (item.Status == WorkItemStatus.Done) continue;
            foreach (var m in item.Members)
            {
                if (byId.TryGetValue(m.SessionId, out var s))
                {
                    live.Add(m.SessionId);
                    if (s.DisplayName != m.Title || (s.Cwd.Length > 0 && s.Cwd != m.Cwd))
                        refresh.Add((item.Id, m.SessionId, s.DisplayName, s.Cwd));
                }
                else if (_sawFirstScan && _liveMembers.Contains(m.SessionId))
                {
                    ended.Add((item.Id, m.SessionId, m.Title));
                }
            }
        }
        _liveMembers = live;
        _sawFirstScan = true;
        if (refresh.Count == 0 && ended.Count == 0) return;

        Task.Run(() =>
        {
            var written = new List<WorkItem>();
            foreach (var group in refresh.GroupBy(r => r.Item))
                if (Store.Mutate(group.Key, i => group.Aggregate(false, (c, r) => i.RefreshMember(r.Session, r.Title, r.Cwd) | c))
                    is { } w) written.Add(w);
            foreach (var e in ended)
                if (Store.Mutate(e.Item, i => i.AddLog($"{Label(e.Title, e.Session)} ended", byPerch: true)) is { } w)
                    written.Add(w);
            return written;
        }).ContinueWith(t =>
        {
            if (_disposed || !t.IsCompletedSuccessfully) return;
            foreach (var w in t.Result) Apply(w);
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>The overlay's view of the items: active ones (oldest first) then paused ones; done items are left
    /// out. Each carries every member id (the overlay groups whichever are on its roster) and the members no longer
    /// on <paramref name="roster"/>, newest first, named from their snapshots.</summary>
    public IReadOnlyList<OverlayWorkItem> ForOverlay(IReadOnlyList<ClaudeSession> roster)
    {
        var onRoster = roster.Select(s => s.SessionId).ToHashSet(StringComparer.Ordinal);
        return Items.Where(i => i.Status != WorkItemStatus.Done)
            .OrderBy(i => i.Status)
            .Select(i => new OverlayWorkItem(i.Id, i.Name, i.Colour, i.Status,
                i.Members.Select(m => m.SessionId).ToHashSet(StringComparer.Ordinal),
                i.Members.Where(m => !onRoster.Contains(m.SessionId))
                    .OrderByDescending(m => m.AddedUtc)
                    .Select(m => new OverlayEndedMember(m.SessionId, Label(m.Title, m.SessionId), m.Cwd))
                    .ToList()))
            .ToList();
    }

    /// <summary>A string that changes whenever <see cref="ForOverlay"/> would draw differently, so the app only
    /// re-pushes the list to the overlay when it must.</summary>
    public static string Signature(IReadOnlyList<OverlayWorkItem> items) =>
        string.Join("|", items.Select(i =>
            $"{i.Id}:{i.Name}:{i.Colour}:{i.Status}:{string.Join(",", i.SessionIds.Order(StringComparer.Ordinal))}"
            + $":{string.Join(",", i.Ended.Select(e => $"{e.SessionId}={e.Title}={e.Cwd}"))}"));

    // How a session is named in the log: its title snapshot, else a short id.
    private static string Label(string? title, string sessionId) =>
        string.IsNullOrWhiteSpace(title) ? $"Session {sessionId[..Math.Min(8, sessionId.Length)]}" : title.Trim();

    // ── Loading ──

    private void Reload()
    {
        if (_disposed) return;
        if (_loading) { _reloadAgain = true; return; }
        _loading = true;
        Task.Run(Store.LoadAll).ContinueWith(t =>
        {
            _loading = false;
            if (_disposed) return;
            if (t.IsCompletedSuccessfully)
            {
                Items = t.Result;
                Changed?.Invoke();
            }
            if (_reloadAgain) { _reloadAgain = false; Reload(); }
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void EnsureWatcher()
    {
        if (_disposed || _watcher is not null || !Directory.Exists(Store.Dir)) return;
        try
        {
            var w = new FileSystemWatcher(Store.Dir, "*.md")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            };
            w.Changed += OnFileEvent;
            w.Created += OnFileEvent;
            w.Deleted += OnFileEvent;
            w.Renamed += OnFileEvent;
            w.Error += (_, _) => Dispatcher.UIThread.Post(() =>
            {
                DisposeWatcher();
                EnsureWatcher();
                Reload();
            });
            w.EnableRaisingEvents = true;
            _watcher = w;
        }
        catch
        {
            _watcher = null;
        }
    }

    // Our own writes come back through here too; reloading after them is harmless (the content is the same).
    private void OnFileEvent(object? sender, FileSystemEventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        if (_disposed) return;
        _settle.Stop();
        _settle.Start();
    });

    private void DisposeWatcher()
    {
        var w = _watcher;
        _watcher = null;
        if (w is null) return;
        try { w.EnableRaisingEvents = false; w.Dispose(); } catch { }
    }

    public void Dispose()
    {
        _disposed = true;
        _settle.Stop();
        DisposeWatcher();
    }
}

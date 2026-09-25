using System.IO;
using Avalonia.Threading;
using Perch.Data;

namespace Perch.Avalonia.Services;

/// <summary>
/// Follows one transcript <c>.jsonl</c> live: a <see cref="FileSystemWatcher"/> on its file, debounced onto the
/// UI thread, driving a <see cref="TranscriptTailReader"/> off it. Every read runs on the thread pool and its
/// <see cref="TailRead"/> is posted back to the UI thread, so the consumer just appends (or, on
/// <see cref="TailRead.Reset"/>, rebuilds). Shared by the history viewer and the Roost's tailed panes.
///
/// <para>Reads are strictly serialised (the reader isn't thread-safe): a change that lands while one is in
/// flight marks the host dirty and triggers exactly one more read afterwards, so the last append of a burst is
/// never dropped. The first read is always delivered, even when empty, so the consumer can leave its "Loading…"
/// state; later ones only when something changed. After <see cref="Dispose"/> nothing is delivered.</para>
/// </summary>
internal sealed class TranscriptTailHost : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(150);

    private readonly TranscriptTailReader _reader;
    private readonly Action<TailRead> _onRead;
    private FileSystemWatcher? _watcher;
    private DispatcherTimer? _debounce;
    private bool _busy, _dirty, _delivered, _disposed;

    /// <param name="path">The transcript to follow.</param>
    /// <param name="initialLines">Trailing lines the first read returns; 0 = the whole file.</param>
    /// <param name="onRead">Called on the UI thread with each read.</param>
    public TranscriptTailHost(string path, int initialLines, Action<TailRead> onRead)
    {
        _reader = new TranscriptTailReader(path, initialLines);
        _onRead = onRead;
    }

    public string Path => _reader.Path;

    /// <summary>Kicks off the first read and, when <paramref name="watch"/>, starts following appends. Call on
    /// the UI thread, once.</summary>
    public void Start(bool watch)
    {
        if (watch) StartWatching();
        RequestRead();
    }

    /// <summary>Forces a read now (e.g. the session reported activity before the watcher fired).</summary>
    public void Poke() => RequestRead();

    private void StartWatching()
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(_reader.Path);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
            _watcher = new FileSystemWatcher(dir, System.IO.Path.GetFileName(_reader.Path))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            };
            _watcher.Changed += OnChanged;
            _watcher.Created += OnChanged;
            _watcher.Renamed += OnChanged;
            _watcher.EnableRaisingEvents = true;
        }
        catch { /* best-effort tailing */ }
    }

    // FileSystemWatcher fires on a background thread and can burst; debounce onto the UI thread.
    private void OnChanged(object? sender, FileSystemEventArgs e) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed) return;
            _debounce ??= CreateDebounce();
            _debounce.Stop();
            _debounce.Start();
        });

    private DispatcherTimer CreateDebounce()
    {
        var t = new DispatcherTimer { Interval = Debounce };
        t.Tick += (_, _) => { t.Stop(); RequestRead(); };
        return t;
    }

    private void RequestRead()
    {
        if (_disposed) return;
        if (_busy) { _dirty = true; return; }
        _busy = true;
        Task.Run(_reader.Read).ContinueWith(t => Dispatcher.UIThread.Post(() =>
        {
            _busy = false;
            if (_disposed) return;
            if (t.IsCompletedSuccessfully && (!_delivered || t.Result.Lines.Count > 0 || t.Result.Reset))
            {
                _delivered = true;
                try { _onRead(t.Result); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"TranscriptTailHost consumer: {ex}"); }
            }
            if (_dirty) { _dirty = false; RequestRead(); }
        }));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _debounce?.Stop();
        if (_watcher is { } w)
        {
            try { w.EnableRaisingEvents = false; } catch { }
            w.Dispose();
        }
    }
}

using System.IO;
using Avalonia.Threading;
using Perch.Data;

namespace Perch.Avalonia.Services;

/// <summary>
/// Follows one transcript <c>.jsonl</c> live: a <see cref="FileSystemWatcher"/> on its file, debounced onto the
/// UI thread, driving a <see cref="TranscriptTailReader"/> off it. Every read <em>and its decode</em> run on the
/// thread pool (the consumer's <c>decode</c> turns a <see cref="TailRead"/> into whatever it folds in — parsing
/// JSON lines is the costly part), and the result is posted back to the UI thread. Drives the Roost's tailed panes
/// (the history viewer tails through <see cref="TranscriptLineTail"/> instead).
///
/// <para>Reads are strictly serialised (the reader isn't thread-safe): a change that lands while one is in
/// flight marks the host dirty and triggers exactly one more read afterwards, so the last append of a burst is
/// never dropped. The first read is always delivered, even when empty, so the consumer can leave its "Loading…"
/// state; later ones only when something changed. After <see cref="Dispose"/> nothing is delivered.</para>
///
/// <para>The watcher is a fast path, not the only one (review fixes CP26): <see cref="Poke"/> forces a read and
/// arms the watcher if it never started (the project folder didn't exist yet), and a watcher that errors (a
/// buffer overflow, its folder going away) is re-armed with a catch-up read.</para>
/// </summary>
internal sealed class TranscriptTailHost<T> : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(150);

    private readonly TranscriptTailReader _reader;
    private readonly Func<TailRead, bool, T> _decode;
    private readonly Action<T> _onRead;
    private FileSystemWatcher? _watcher;
    private DispatcherTimer? _debounce;
    private bool _watch, _busy, _dirty, _delivered, _disposed;

    /// <param name="path">The transcript to follow.</param>
    /// <param name="initialLines">Trailing lines the first read returns; 0 = the whole file.</param>
    /// <param name="decode">Runs on the thread pool for each read worth delivering: (read, is the first read).</param>
    /// <param name="onRead">Called on the UI thread with each decoded read.</param>
    public TranscriptTailHost(string path, int initialLines, Func<TailRead, bool, T> decode, Action<T> onRead)
    {
        _reader = new TranscriptTailReader(path, initialLines);
        _decode = decode;
        _onRead = onRead;
    }

    public string Path => _reader.Path;

    /// <summary>Kicks off the first read and, when <paramref name="watch"/>, starts following appends. Call on
    /// the UI thread, once.</summary>
    public void Start(bool watch)
    {
        _watch = watch;
        if (watch) StartWatching();
        RequestRead();
    }

    /// <summary>Forces a read now (the session reported activity, the watcher may have missed it) and arms the
    /// watcher if it isn't running. UI thread.</summary>
    public void Poke()
    {
        if (_disposed) return;
        if (_watch && _watcher is null) StartWatching();
        RequestRead();
    }

    private void StartWatching()
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(_reader.Path);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;   // a later Poke tries again
            var w = new FileSystemWatcher(dir, System.IO.Path.GetFileName(_reader.Path))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            };
            w.Changed += OnChanged;
            w.Created += OnChanged;
            w.Renamed += OnChanged;
            w.Error += OnWatcherError;
            w.EnableRaisingEvents = true;
            _watcher = w;
        }
        catch { /* best-effort tailing: the next Poke tries again */ }
    }

    // A dead watcher (buffer overflow, folder deleted) raises nothing more: drop it, re-arm, and read once to
    // catch up on whatever it missed.
    private void OnWatcherError(object? sender, ErrorEventArgs e) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed || !ReferenceEquals(sender, _watcher)) return;
            DisposeWatcher();
            StartWatching();
            RequestRead();
        });

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
        bool first = !_delivered;
        Task.Run(() =>
        {
            var read = _reader.Read();
            bool deliver = first || read.Lines.Count > 0 || read.Reset;
            return (deliver, decoded: deliver ? _decode(read, first) : default);
        }).ContinueWith(t => Dispatcher.UIThread.Post(() =>
        {
            _busy = false;
            if (_disposed) return;
            if (t.IsCompletedSuccessfully && t.Result.deliver)
            {
                _delivered = true;
                try { _onRead(t.Result.decoded!); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"TranscriptTailHost consumer: {ex}"); }
            }
            if (_dirty) { _dirty = false; RequestRead(); }
        }));
    }

    private void DisposeWatcher()
    {
        if (_watcher is not { } w) return;
        _watcher = null;
        try { w.EnableRaisingEvents = false; } catch { }
        w.Changed -= OnChanged;
        w.Created -= OnChanged;
        w.Renamed -= OnChanged;
        w.Error -= OnWatcherError;
        w.Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _debounce?.Stop();
        DisposeWatcher();
    }
}

using System.Diagnostics;
using Avalonia.Threading;
using Perch.Data;
using Perch.Data.Sprig;

namespace Perch.Avalonia.Services;

/// <summary>
/// Keeps one <c>sprig watch --json</c> running while the Sprig integration is on and turns its lines into a current
/// <see cref="SprigStatus"/> for the overlay. See docs/sprig-integration-plan.md.
/// </summary>
/// <remarks>
/// <para><b>One long-lived child, never a poll.</b> Launching the CLI on a timer costs a .NET start each time and is
/// the pattern endpoint security scores against. <c>watch</c> sends a snapshot, then a line per change, and is quiet
/// otherwise. Its stdin stays redirected and open: sprig ends the watch when that pipe closes, so Perch exiting or
/// crashing takes the child with it.</para>
///
/// <para><b>Threads.</b> A background loop owns the process and the <see cref="SprigStatusFold"/>. Each change it
/// reads is parked in <c>_latest</c> and at most one post to the UI thread is pending at a time, so a burst of lines
/// (a workspace starting) costs one <see cref="Changed"/>, not one per line.</para>
///
/// <para><b>Restarts.</b> If <c>watch</c> exits it's started again after <see cref="FirstBackoff"/>, doubling to
/// <see cref="MaxBackoff"/>; a run that lasted <see cref="SettledRun"/> resets the wait. Meanwhile the last state
/// stays up, marked <see cref="SprigFeed.Reconnecting"/>. With no sprig installed it looks again every
/// <see cref="MissingRetry"/>.</para>
/// </remarks>
internal sealed class SprigMonitorHost : IDisposable
{
    private static readonly TimeSpan FirstBackoff = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan SettledRun = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MissingRetry = TimeSpan.FromMinutes(1);
    private const string LogFile = "sprig.log";

    private readonly Lock _gate = new();
    private CancellationTokenSource? _cts;
    private Process? _process;
    private int _generation;                      // bumped by Start/Stop, so a post from an older run is dropped
    // Swapped whole, so the UI thread never reads a status from one moment with a feed state from another.
    private sealed record Parked(int Generation, SprigStatus Status, SprigFeed Feed);
    private Parked _latest = new(0, SprigStatus.Empty, SprigFeed.Off);
    private int _postPending;
    private bool _disposed;

    /// <summary>The latest state read from sprig. <see cref="SprigStatus.Empty"/> while off or before the first
    /// snapshot.</summary>
    public SprigStatus Status { get; private set; } = SprigStatus.Empty;

    /// <summary>Where the connection to sprig stands.</summary>
    public SprigFeed Feed { get; private set; } = SprigFeed.Off;

    /// <summary>Raised on the UI thread when <see cref="Status"/> or <see cref="Feed"/> changed.</summary>
    public event Action? Changed;

    public bool Running => _cts is not null;

    /// <summary>Starts watching. Idempotent; call on the UI thread.</summary>
    public void Start()
    {
        if (_disposed || _cts is not null) return;
        var cts = _cts = new CancellationTokenSource();
        int generation = Interlocked.Increment(ref _generation);
        Publish(generation, SprigStatus.Empty, SprigFeed.Connecting);
        _ = Task.Run(() => RunAsync(generation, cts.Token));
    }

    /// <summary>Stops watching and clears the state, so turning the setting off empties the section at once.</summary>
    public void Stop()
    {
        if (_cts is not { } cts) return;
        _cts = null;
        int generation = Interlocked.Increment(ref _generation);
        cts.Cancel();
        EndProcess();
        Volatile.Write(ref _latest, new Parked(generation, SprigStatus.Empty, SprigFeed.Off));
        Status = SprigStatus.Empty;
        Feed = SprigFeed.Off;
        Changed?.Invoke();
    }

    private async Task RunAsync(int generation, CancellationToken token)
    {
        var fold = new SprigStatusFold();
        var backoff = FirstBackoff;
        while (!token.IsCancellationRequested)
        {
            if (SprigCli.Locate() is not { } exe)
            {
                Publish(generation, fold.Current, SprigFeed.NotInstalled);
                if (!await Delay(MissingRetry, token)) return;
                continue;
            }

            var started = Stopwatch.StartNew();
            await WatchOnceAsync(exe, generation, fold, token);
            if (token.IsCancellationRequested) return;

            // Keep the last state on screen while it comes back; a fresh watch opens with a snapshot anyway.
            Publish(generation, fold.Current, SprigFeed.Reconnecting);
            backoff = started.Elapsed >= SettledRun ? FirstBackoff : backoff;
            if (!await Delay(backoff, token)) return;
            backoff = TimeSpan.FromTicks(Math.Min(MaxBackoff.Ticks, backoff.Ticks * 2));
        }
    }

    // One run of `sprig watch --json`, until it exits or the host stops.
    private async Task WatchOnceAsync(string exe, int generation, SprigStatusFold fold, CancellationToken token)
    {
        Process process;
        try
        {
            process = Process.Start(SprigCli.WatchStartInfo(exe)) ?? throw new InvalidOperationException("no process");
        }
        catch (Exception e)
        {
            DiagnosticLog.Append(LogFile, $"watch didn't start: {e.Message}");
            return;
        }

        lock (_gate) _process = process;
        // Drained so a chatty stderr can never fill its pipe and stall sprig; kept for the log when it ends badly.
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        bool live = false;
        try
        {
            while (await process.StandardOutput.ReadLineAsync(token) is { } line)
            {
                if (!fold.Apply(line)) continue;
                live |= fold.Current.Seen;
                Publish(generation, fold.Current, live ? SprigFeed.Live : SprigFeed.Connecting);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        finally
        {
            lock (_gate) if (ReferenceEquals(_process, process)) _process = null;
            Close(process);
            if (!token.IsCancellationRequested)
            {
                var err = stderr.IsCompletedSuccessfully ? stderr.Result.Trim() : "";
                DiagnosticLog.Append(LogFile, $"watch ended (exit {(process.HasExited ? process.ExitCode : -1)})"
                                              + (err.Length > 0 ? $": {Truncate(err, 400)}" : ""));
            }
            process.Dispose();
        }
    }

    // Parks the newest state and makes sure one post to the UI thread is on its way.
    private void Publish(int generation, SprigStatus status, SprigFeed feed)
    {
        Volatile.Write(ref _latest, new Parked(generation, status, feed));
        if (Interlocked.Exchange(ref _postPending, 1) == 0)
            Dispatcher.UIThread.Post(Flush);
    }

    private void Flush()
    {
        Interlocked.Exchange(ref _postPending, 0);
        var (generation, status, feed) = Volatile.Read(ref _latest);
        if (_disposed || generation != Volatile.Read(ref _generation)) return;
        if (ReferenceEquals(status, Status) && feed == Feed) return;
        Status = status;
        Feed = feed;
        Changed?.Invoke();
    }

    private void EndProcess()
    {
        Process? process;
        lock (_gate) { process = _process; _process = null; }
        if (process is not null) _ = Task.Run(() => Close(process));
    }

    // Closing stdin is sprig's own "the consumer has gone" signal; a kill is only the fallback.
    private static void Close(Process process)
    {
        try
        {
            if (process.HasExited) return;
            try { process.StandardInput.Close(); } catch { /* already closed */ }
            if (!process.WaitForExit(2000)) process.Kill();
        }
        catch { /* already gone */ }
    }

    private static async Task<bool> Delay(TimeSpan wait, CancellationToken token)
    {
        try { await Task.Delay(wait, token); return true; }
        catch (OperationCanceledException) { return false; }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    public void Dispose()
    {
        if (_disposed) return;
        Stop();
        _disposed = true;
    }
}

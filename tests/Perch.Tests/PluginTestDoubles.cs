using System.Text;
using Perch.Plugins;

namespace Perch.Tests;

/// <summary>
/// An <see cref="IPluginProcess"/> backed by in-memory streams so the protocol driver and service can be
/// tested with no real process. Canned stdout is returned line by line; stdin is captured for assertions.
/// </summary>
internal sealed class FakePluginProcess : IPluginProcess
{
    private readonly StringWriter _stdin = new();
    private readonly TextReader _stdout;
    private readonly int _exitCode;
    private readonly bool _hang;
    public bool Killed { get; private set; }

    /// <param name="stdoutLines">Lines the fake plugin "prints" (joined with \n).</param>
    /// <param name="exitCode">Exit code reported by WaitForExitAsync.</param>
    /// <param name="hang">If true, stdout never ends and WaitForExit never completes (simulates a hung
    /// plugin) so the session's timeout path can be exercised.</param>
    public FakePluginProcess(IEnumerable<string> stdoutLines, int exitCode = 0, bool hang = false)
    {
        _stdout = hang ? new NeverEndingReader() : new StringReader(string.Join('\n', stdoutLines));
        _exitCode = exitCode;
        _hang = hang;
    }

    public TextWriter StandardInput => _stdin;
    public TextReader StandardOutput => _stdout;
    public string CapturedStdin => _stdin.ToString();

    public async Task<int> WaitForExitAsync(CancellationToken ct)
    {
        if (_hang) { await Task.Delay(Timeout.Infinite, ct); }
        return _exitCode;
    }

    public void Kill() => Killed = true;
    public void Dispose() { }

    // A reader whose ReadLineAsync never completes — used to simulate a plugin that opens stdout but
    // neither writes nor exits.
    private sealed class NeverEndingReader : TextReader
    {
        public override Task<string?> ReadLineAsync() => new TaskCompletionSource<string?>().Task;
    }
}

/// <summary>An <see cref="IPluginSandbox"/> that hands back a pre-programmed <see cref="FakePluginProcess"/>
/// and records the launch spec it was asked for.</summary>
internal sealed class FakePluginSandbox(FakePluginProcess process) : IPluginSandbox
{
    public PluginLaunchSpec? LastSpec { get; private set; }

    public IPluginProcess Launch(PluginLaunchSpec spec)
    {
        LastSpec = spec;
        return process;
    }
}

using Perch.Data;
using Perch.Data.Replay;

namespace Perch.Avalonia.Services.Replay;

/// <summary>
/// The <c>perch replay &lt;recording&gt;</c> entry point. Runs at the very top of <c>Main</c>, ahead of
/// the Velopack/mutex work, because it must point <c>CLAUDE_CONFIG_DIR</c> at a disposable sandbox and
/// install the virtual <see cref="Clock"/> + projector process-probe <em>before</em> anything touches
/// <see cref="ClaudePaths"/> (which snapshots the config dir on first access). After
/// <see cref="Prepare"/> succeeds, the normal app boots and drives the sandbox exactly as if it were a
/// live <c>~/.claude</c>.
/// </summary>
internal static class ReplayBootstrap
{
    /// <summary>
    /// Loads the recording, stands up the sandbox, installs the replay clock + probe, and materialises
    /// scene position 0. Returns false (with a message on stderr) when the recording can't be loaded, so
    /// the caller can exit cleanly.
    /// </summary>
    public static bool Prepare(string? recordingPath)
    {
        if (string.IsNullOrWhiteSpace(recordingPath))
        {
            Console.Error.WriteLine("usage: perch replay <recording.perchreplay>");
            return false;
        }

        var recording = Recording.Load(recordingPath);
        if (recording == null)
        {
            Console.Error.WriteLine($"Not a readable .perchreplay recording: {recordingPath}");
            return false;
        }

        SweepOrphanedSandboxes();

        var sandbox = Path.Combine(
            Path.GetTempPath(), "perch-replay-sandbox-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(sandbox, "sessions"));
        Directory.CreateDirectory(Path.Combine(sandbox, "projects"));

        // The whole game is ordering: set this before ClaudePaths is first read so the entire reader
        // stack follows the sandbox.
        Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", sandbox);

        var clock = new ReplayClock(recording.SceneEpochUtc);
        Clock.SetProvider(clock);

        var projector = new Projector(recording, clock, sandbox);
        projector.MaterialiseAt(0);

        ReplaySession.Current = new ReplaySession
        {
            Recording = recording,
            Clock = clock,
            Projector = projector,
            SandboxDir = sandbox,
        };
        return true;
    }

    // Removes replay sandboxes left behind by a prior force-killed run (which skips graceful Cleanup).
    // Age-gated to an hour so a replay instance running concurrently in another window isn't disturbed.
    // Wall time, not the virtual clock (which isn't installed yet at this point anyway).
    private static void SweepOrphanedSandboxes()
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddHours(-1);
            foreach (var dir in Directory.EnumerateDirectories(Path.GetTempPath(), "perch-replay-sandbox-*"))
            {
                try { if (Directory.GetLastWriteTimeUtc(dir) < cutoff) Directory.Delete(dir, recursive: true); }
                catch { }
            }
        }
        catch { }
    }

    /// <summary>Tears down the replay sandbox on exit — the temp tree is disposable and never reused.</summary>
    public static void Cleanup()
    {
        var session = ReplaySession.Current;
        if (session == null)
            return;
        session.Recording.Dispose();
        try { Directory.Delete(session.SandboxDir, recursive: true); } catch { }
        ReplaySession.Current = null;
    }
}

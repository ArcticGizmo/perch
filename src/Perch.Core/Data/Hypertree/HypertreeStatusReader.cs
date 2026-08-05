using System.Diagnostics;
using System.Text.Json;

namespace Perch.Data.Hypertree;

/// <summary>
/// Reads Hypertree's published <c>status.json</c> — the layout and the live cursor position behind the
/// overlay's Hypertree strip.
/// </summary>
/// <remarks>
/// <para><b>Why the file and not <c>htree list --json</c>.</b> Hypertree publishes this file expressly for
/// outside readers, and keeps it current even for desktop switches it didn't make. Reads are therefore
/// free and watchable, whereas a process spawn per refresh is far too expensive for a marker the overlay
/// keeps live. Only the one mutating action (a jump) needs the CLI — see <see cref="HypertreeBridge"/>.</para>
///
/// <para>Hypertree writes atomically (temp file + replace), so torn content isn't a failure mode here;
/// the only transient is the microscopic window where the replace is in flight and the path can't be
/// opened, which the retry covers. Everything else follows the house rule for the <c>~/.claude</c>
/// readers: never throw out of a scan, and treat anything unexpected as "nothing to show".</para>
/// </remarks>
internal static class HypertreeStatusReader
{
    /// <summary>The only contract version Perch understands. A newer Hypertree is ignored rather than
    /// half-parsed — the strip disappears, which is honest, instead of rendering a guess.</summary>
    public const int SupportedSchema = 1;

    /// <summary>Hypertree's own escape hatch for relocating its state directory. Honoured so a Perch
    /// pointed at a scratch Hypertree (or a portable install) finds the same file the tray writes.</summary>
    private const string DirectoryVariable = "HYPERTREE_STATE_DIR";

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    /// <summary>The directory Hypertree keeps its state in. Never created — its absence is an answer.</summary>
    public static string Directory
    {
        get
        {
            var over = Environment.GetEnvironmentVariable(DirectoryVariable);
            if (!string.IsNullOrWhiteSpace(over)) return over;
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "hypertree");
        }
    }

    public const string FileName = "status.json";

    /// <summary>Full path to the status file. Its absence means no Hypertree tray has run, or one exited
    /// cleanly — either way there is nothing to show.</summary>
    public static string FilePath => Path.Combine(Directory, FileName);

    /// <summary>
    /// The published status, or null when there is none to trust: no file, a schema we don't know, a file
    /// left behind by a crashed tray (dead pid), or unparseable content.
    /// </summary>
    public static HypertreeStatus? Read()
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var path = FilePath;
                if (!File.Exists(path)) return null;

                // FileShare.ReadWrite in the house style: the writer is live, and we must never be the
                // reason its replace fails.
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var status = JsonSerializer.Deserialize<HypertreeStatus>(stream, Options);

                if (status is null) return null;
                if (status.Schema != SupportedSchema) return null;
                return IsAlive(status.Pid) ? status : null;
            }
            catch (IOException) { Thread.Sleep(15); } // mid-replace — try again
            catch { return null; }                    // malformed / unreadable — treat as no status
        }
        return null;
    }

    /// <summary>
    /// Whether the tray that wrote the file is still running. A clean exit deletes the file, but a kill
    /// can't — so trusting mere existence would leave the overlay showing a stack, and a "you are here"
    /// marker, for a Hypertree that has gone.
    /// </summary>
    private static bool IsAlive(int pid)
    {
        if (pid <= 0) return false;
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch { return false; } // no such process, or not visible to us — don't trust the file
    }
}

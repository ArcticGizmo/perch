using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Perch.Data.Control;

/// <summary>What a <c>{sessionId}.perch-lock</c> sidecar records: which Perch process took ownership of
/// the session, where it runs, and when.</summary>
internal sealed record SessionLockInfo(string SessionId, int Pid, string Cwd, string Profile, DateTime Since)
{
    /// <summary>True when the owning process is still alive — a lock left behind by a crashed tray is stale
    /// and must never block a resume.</summary>
    public bool IsLive => SessionLock.IsProcessAlive(Pid);

    /// <summary>True when this very process wrote the lock.</summary>
    public bool IsOurs => Pid == Environment.ProcessId;
}

/// <summary>
/// The cross-process ownership marker for a Perch-controlled session (collision defence (b) in
/// <c>docs/session-ui-plan.md</c>): <c>~/.claude/sessions/{sessionId}.perch-lock</c>, written when Perch
/// takes a session over stream-json and deleted when it lets go. <see cref="ControlledSessions"/> is the
/// in-process registry; this is its on-disk twin, so <c>perch-hook start</c> can warn when a <em>normal</em>
/// <c>claude --resume</c> opens an id Perch is already driving (two writers on one transcript), and a second
/// Perch instance (dev + release) can see each other's sessions. Liveness is by PID probe — a lock whose
/// owner has exited is stale and is treated as absent (and swept). Best-effort throughout: a lock that can't
/// be written never stops a session from starting.
/// </summary>
internal static class SessionLock
{
    public const string Extension = ".perch-lock";

    /// <summary>The environment variable Perch sets on the sessions it controls, carrying the owning
    /// tray's PID, so <c>perch-hook start</c> can tell "Perch's own session starting" (the lock is ours)
    /// from "a normal claude opened a Perch-controlled id" (warn).</summary>
    public const string OwnerEnvVar = "PERCH_SESSION_OWNER";

    /// <summary>The lock sidecar path in a session's owning <c>sessions/</c> dir. A null
    /// <paramref name="sessionsDir"/> means the primary (<see cref="ClaudePaths.SessionsDir"/>) — the
    /// common case; a non-primary config dir passes its own so the lock lands beside the session.</summary>
    public static string PathFor(string sessionId, string? sessionsDir = null) =>
        Path.Combine(sessionsDir ?? ClaudePaths.SessionsDir, sessionId + Extension);

    /// <summary>Writes the lock for <paramref name="sessionId"/>, owned by this process. Returns false when
    /// another <em>live</em> process already holds it (the caller should refuse to control the session) or
    /// the write failed; a stale lock is overwritten. <paramref name="sessionsDir"/> targets a non-primary
    /// config dir's sessions folder (null = primary).</summary>
    public static bool Acquire(string sessionId, string cwd, string? sessionsDir = null)
    {
        if (string.IsNullOrEmpty(sessionId)) return false;
        try
        {
            if (Read(sessionId, sessionsDir) is { } existing && existing.IsLive && !existing.IsOurs) return false;
            var dir = sessionsDir ?? ClaudePaths.SessionsDir;
            Directory.CreateDirectory(dir);
            var o = new JsonObject
            {
                ["sessionId"] = sessionId,
                // A string so perch-hook's flat string-field reader can consume it without a numeric path.
                ["pid"] = Environment.ProcessId.ToString(),
                ["cwd"] = cwd,
                ["profile"] = AppProfile.DataFolderName,
                ["since"] = DateTime.UtcNow.ToString("o"),
            };
            File.WriteAllText(PathFor(sessionId, sessionsDir), o.ToJsonString());
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Deletes the lock if this process holds it (or it is stale). Another live owner's lock is
    /// left alone. <paramref name="sessionsDir"/> targets the owning config dir (null = primary).</summary>
    public static void Release(string? sessionId, string? sessionsDir = null)
    {
        if (string.IsNullOrEmpty(sessionId)) return;
        try
        {
            var info = Read(sessionId, sessionsDir);
            if (info is null || info.IsOurs || !info.IsLive) File.Delete(PathFor(sessionId, sessionsDir));
        }
        catch { /* best effort */ }
    }

    /// <summary>Reads the lock, or null when absent/unreadable. <paramref name="sessionsDir"/> targets the
    /// owning config dir (null = primary).</summary>
    public static SessionLockInfo? Read(string sessionId, string? sessionsDir = null)
    {
        try
        {
            var path = PathFor(sessionId, sessionsDir);
            if (!File.Exists(path)) return null;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs);
            if (JsonNode.Parse(reader.ReadToEnd()) is not JsonObject o) return null;
            int.TryParse(TranscriptJson.AsString(o["pid"]), out int pid);
            DateTime.TryParse(TranscriptJson.AsString(o["since"]), null,
                System.Globalization.DateTimeStyles.RoundtripKind, out var since);
            return new SessionLockInfo(
                TranscriptJson.AsString(o["sessionId"]) ?? sessionId,
                pid,
                TranscriptJson.AsString(o["cwd"]) ?? "",
                TranscriptJson.AsString(o["profile"]) ?? "",
                since);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The live owner of <paramref name="sessionId"/> other than this process, or null when the
    /// session is free (no lock, our own lock, or a stale one). <paramref name="sessionsDir"/> targets the
    /// owning config dir (null = primary).</summary>
    public static SessionLockInfo? HeldByOther(string sessionId, string? sessionsDir = null) =>
        Read(sessionId, sessionsDir) is { } info && info.IsLive && !info.IsOurs ? info : null;

    /// <summary>Deletes every stale lock (owning process exited) across <b>every</b> distinct
    /// <c>sessions/</c> dir in the config-dir set. Run at tray start-up so a crash never leaves phantom
    /// ownership behind. Returns how many were removed.</summary>
    public static int SweepStale()
    {
        int removed = 0;
        foreach (var sessionsDir in ClaudeConfigSet.Instance.DistinctSessionsDirs())
        {
            try
            {
                if (!Directory.Exists(sessionsDir)) continue;
                foreach (var file in Directory.EnumerateFiles(sessionsDir, "*" + Extension))
                {
                    var id = Path.GetFileName(file);
                    id = id[..^Extension.Length];
                    var info = Read(id, sessionsDir);
                    if (info is null || !info.IsLive)
                    {
                        try { File.Delete(file); removed++; } catch { }
                    }
                }
            }
            catch { /* best effort — one bad dir must not sink the sweep */ }
        }
        return removed;
    }

    public static bool IsProcessAlive(int pid)
    {
        if (pid <= 0) return false;
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch
        {
            return false;
        }
    }
}

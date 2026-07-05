using System.Text.Json;

// perch-hook <event>
//
// A tiny, fast Claude Code hook writer. Reads the hook JSON payload on stdin and writes the sidecar files
// the Perch tray watches under <claude-config>/sessions. Honours CLAUDE_CONFIG_DIR exactly like the tray
// (Perch.Data.ClaudePaths) so a relocated config — or a dev/test config — is followed correctly.
//
// Two invariants keep a stale hook from ever wedging a Claude Code session:
//   * it always exits 0, and
//   * it never writes a "block" decision to stdout.
// So even pointed at a torn-down Perch, the worst it does is nothing.

try
{
    string action = args.Length > 0 ? args[0] : "";
    byte[] payload = ReadStdin();
    string sessionsDir = Path.Combine(ResolveClaudeDir(), "sessions");

    switch (action)
    {
        case "mode":
        {
            // The hot path: fired on every PreToolUse / PostToolUse / Stop. Record the session's
            // permission mode so the overlay can badge it. Nothing else from the payload is read.
            var (sid, mode) = ReadTwo(payload, "session_id", "permission_mode");
            if (!string.IsNullOrEmpty(sid) && !string.IsNullOrEmpty(mode) && Directory.Exists(sessionsDir))
                File.WriteAllText(Path.Combine(sessionsDir, sid + ".mode"), mode);
            break;
        }

        // The remaining events (agentstop, teammateidle, start, cleanup) will be ported from invoke.ps1
        // once the perf verdict is in — `mode` is the representative hot path this spike measures.
        default:
            break;
    }
}
catch { /* never fail a hook */ }

return 0;

static byte[] ReadStdin()
{
    using var stdin = Console.OpenStandardInput();
    using var ms = new MemoryStream();
    stdin.CopyTo(ms);
    return ms.ToArray();
}

// Mirrors Perch.Data.ClaudePaths.ResolveClaudeDir: CLAUDE_CONFIG_DIR if set, else ~/.claude.
static string ResolveClaudeDir()
{
    var dir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
    return string.IsNullOrWhiteSpace(dir)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude")
        : dir;
}

// Pulls two top-level string properties from the payload with a forward-only reader — no reflection, no
// allocations beyond the two strings, so it's AOT/trim-safe and about as fast as JSON parsing gets.
static (string? a, string? b) ReadTwo(byte[] json, string nameA, string nameB)
{
    string? a = null, b = null;
    try
    {
        var reader = new Utf8JsonReader(json);
        int depth = 0;
        string? prop = null;
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                case JsonTokenType.StartArray:
                    depth++;
                    break;
                case JsonTokenType.EndObject:
                case JsonTokenType.EndArray:
                    depth--;
                    prop = null;
                    break;
                case JsonTokenType.PropertyName:
                    prop = depth == 1 ? reader.GetString() : null;
                    break;
                case JsonTokenType.String:
                    if (depth == 1 && prop is not null)
                    {
                        if (prop == nameA) a = reader.GetString();
                        else if (prop == nameB) b = reader.GetString();
                    }
                    prop = null;
                    break;
                default:
                    prop = null;
                    break;
            }
        }
    }
    catch { }
    return (a, b);
}

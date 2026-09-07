using System.Text.Json.Nodes;

namespace Perch.Data.Control;

/// <summary>
/// The wire contract between a second <c>perch</c> launch acting as a CLI (<c>perch --resume &lt;id&gt;</c>,
/// <c>perch -c</c>, <c>perch [dir]</c> — the <c>claude</c>-shaped arguments, docs/session-ui-plan.md Phase 3)
/// and the running tray: a local named pipe (<see cref="PipeName"/>), one newline-delimited JSON
/// <see cref="SessionOpenIntent"/> per connection, one <see cref="ControlReply"/> line back. Modelled on
/// <see cref="ValetProtocol"/>; per-profile pipe names keep a dev tray and an installed one apart.
/// </summary>
internal static class ControlProtocol
{
    /// <summary>This profile's pipe name — <c>perch-control</c> or <c>perch-control-dev</c>.</summary>
    public static string PipeName => AppProfile.IsDev ? "perch-control-dev" : "perch-control";
}

/// <summary>
/// "Open a Perch session window" as parsed from claude-like command-line arguments. Exactly one of the
/// shapes: resume a specific id (<see cref="ResumeId"/>), pick a session to resume (<see cref="PickResume"/>,
/// a bare <c>--resume</c>), continue the folder's most recent session (<see cref="Continue"/>), or start
/// fresh in <see cref="Cwd"/>. <see cref="Model"/> / <see cref="PermissionMode"/> ride along when given.
/// </summary>
internal sealed record SessionOpenIntent(
    string Cwd,
    string? ResumeId = null,
    bool PickResume = false,
    bool Continue = false,
    string? Model = null,
    string? PermissionMode = null)
{
    /// <summary>Parses claude-shaped arguments. Returns null when nothing in <paramref name="args"/> asks for
    /// a session (no recognised flag and no existing-directory positional) — unknown flags such as
    /// <c>--autostarted</c> are ignored rather than treated as an intent, so ordinary tray launches are
    /// unaffected. A positional that is an existing directory becomes the working directory; otherwise
    /// <paramref name="currentDir"/> is used.</summary>
    public static SessionOpenIntent? FromArgs(IReadOnlyList<string> args, string currentDir)
    {
        string? cwd = null, resumeId = null, model = null, mode = null;
        bool pick = false, cont = false, any = false;

        for (int i = 0; i < args.Count; i++)
        {
            var a = args[i];
            string? Next() => i + 1 < args.Count && !args[i + 1].StartsWith('-') ? args[++i] : null;
            switch (a)
            {
                case "--resume" or "-r":
                    any = true;
                    var id = Next();
                    if (id is not null && IsSessionId(id)) resumeId = id;
                    else
                    {
                        if (id is not null) i--;   // wasn't an id — leave it for the positional pass
                        pick = true;
                    }
                    break;
                case "--continue" or "-c":
                    any = true;
                    cont = true;
                    break;
                case "--session-id":
                    any = true;
                    if (Next() is { } sid && IsSessionId(sid)) resumeId = sid;
                    break;
                case "--model":
                    if (Next() is { } m && IsToken(m)) { model = m; any = true; }
                    break;
                case "--permission-mode":
                    if (Next() is { } pm && IsToken(pm)) { mode = pm; any = true; }
                    break;
                default:
                    if (!a.StartsWith('-') && cwd is null && Directory.Exists(a))
                    {
                        cwd = Path.GetFullPath(a);
                        any = true;
                    }
                    break;
            }
        }

        if (!any) return null;
        return new SessionOpenIntent(cwd ?? currentDir, resumeId, pick && resumeId is null, cont && resumeId is null, model, mode);
    }

    public string ToJson()
    {
        var o = new JsonObject { ["cwd"] = Cwd };
        if (ResumeId is not null) o["resume"] = ResumeId;
        if (PickResume) o["pick"] = true;
        if (Continue) o["continue"] = true;
        if (Model is not null) o["model"] = Model;
        if (PermissionMode is not null) o["mode"] = PermissionMode;
        return o.ToJsonString();
    }

    public static SessionOpenIntent? Parse(string line)
    {
        try
        {
            if (JsonNode.Parse(line) is not JsonObject o) return null;
            var cwd = TranscriptJson.AsString(o["cwd"]);
            if (string.IsNullOrEmpty(cwd)) return null;
            return new SessionOpenIntent(
                cwd,
                TranscriptJson.AsString(o["resume"]),
                o["pick"]?.GetValue<bool>() ?? false,
                o["continue"]?.GetValue<bool>() ?? false,
                TranscriptJson.AsString(o["model"]),
                TranscriptJson.AsString(o["mode"]));
        }
        catch
        {
            return null;
        }
    }

    // Session ids are UUIDs; the CLI tolerates anything identifier-ish, and so do we (the controller
    // re-validates before it ever reaches a command line).
    private static bool IsSessionId(string s) =>
        s.Length >= 8 && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static bool IsToken(string s) =>
        s.Length > 0 && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_');
}

/// <summary>The tray's one-line answer to a <see cref="SessionOpenIntent"/>.</summary>
internal sealed record ControlReply(bool Ok, string Message)
{
    public string ToJson() => new JsonObject { ["ok"] = Ok, ["message"] = Message }.ToJsonString();

    public static ControlReply? Parse(string line)
    {
        try
        {
            if (JsonNode.Parse(line) is not JsonObject o) return null;
            return new ControlReply(o["ok"]?.GetValue<bool>() ?? false, TranscriptJson.AsString(o["message"]) ?? "");
        }
        catch
        {
            return null;
        }
    }
}

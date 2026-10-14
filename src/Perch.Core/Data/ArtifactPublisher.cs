using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Perch.Data;

/// <summary>A page published to claude.ai by the <c>Artifact</c> tool, as its transcript result records it.</summary>
public sealed record PublishedArtifact(string Url, string Path, string? Title, bool Updated);

/// <summary>How a background publish ended: the artifact, or why there isn't one. <see cref="BackgroundId"/> is the
/// <c>claude --bg</c> short id, kept on failure (the session is left running so <c>claude attach</c> can show why).</summary>
public sealed record ArtifactPublishOutcome(PublishedArtifact? Artifact, string? Error, string? BackgroundId = null);

/// <summary>
/// Publishes a local page as a claude.ai artifact without a terminal. The <c>Artifact</c> tool isn't registered in the
/// headless <c>claude -p</c> sessions Perch drives, but it is in a <em>background</em> session (<c>claude --bg</c>),
/// so this starts one with <see cref="ArtifactPublish.Prompt"/> and the needed tools pre-approved (a permission prompt
/// would stall it, with nobody to answer), maps its short id to a session id through <c>claude agents --json</c>,
/// watches that session's transcript for the tool's structured result (<c>toolUseResult.url</c>), then stops and
/// removes the session. Used by the session window's "Publish as artifact" and by <c>perch artifact publish</c> (which
/// the bundled skill calls from inside a controlled session). Never throws; every failure is an outcome.
/// </summary>
public static class ArtifactPublisher
{
    /// <summary>How long a publish may take before it's given up on.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(2);

    // Inherited from a parent claude / Perch session; the nested background session must start clean, or perch-hook
    // reads it as a Perch-controlled session starting (PERCH_SESSION_OWNER) and the CLI as a nested one.
    private static readonly string[] ScrubbedEnv =
    [
        Control.SessionLock.OwnerEnvVar, PerchSessionPlugin.SessionEnvVar, PerchSessionPlugin.ExeEnvVar,
        "CLAUDECODE", "CLAUDE_CODE_ENTRYPOINT", "CLAUDE_CODE_SSE_PORT",
    ];

    /// <summary>Publishes <paramref name="absPath"/>. With <paramref name="parentSessionId"/> (the session it's for), the
    /// publish is recorded in <see cref="PublishLedger"/>: shown under that session while it runs, and its artifact
    /// merged into that session's glyph afterwards.</summary>
    public static async Task<ArtifactPublishOutcome> PublishAsync(string cwd, string absPath, string? configDir,
        string? parentSessionId = null, CancellationToken ct = default)
    {
        var outcome = await PublishCoreAsync(cwd, absPath, configDir, parentSessionId, ct);
        if (outcome.Artifact is { } a && parentSessionId is not null) PublishLedger.RecordPublished(parentSessionId, a);
        return outcome;
    }

    private static async Task<ArtifactPublishOutcome> PublishCoreAsync(string cwd, string absPath, string? configDir,
        string? parentSessionId, CancellationToken ct)
    {
        if (!File.Exists(absPath)) return new(null, $"{absPath} doesn't exist");
        var prompt = ArtifactPublish.BackgroundPrompt(absPath);
        if (!ClaudeCli.IsSafePrompt(prompt)) return new(null, "the file's path has characters a command line can't carry");

        string? shortId;
        try
        {
            var args = "--bg --model haiku --allowedTools=ToolSearch,Artifact,Read,Glob ";
            if (NoWorktreeSettings() is { } settings) args += $"--settings {ClaudeCli.QuotePrompt(settings)} ";
            var start = await RunClaudeAsync(cwd, configDir, args + ClaudeCli.QuotePrompt(prompt), waitForExit: false, ct);
            shortId = ParseBackgroundId(start);
            if (shortId is null) return new(null, "claude didn't start a background session" + Tail(start));
        }
        catch (Exception ex) { return new(null, ex.Message); }

        // From here the worker is "Perch publishing for this session": a child row under it, out of the daemon strip.
        // Ended in every case below. On failure the worker is left running for `claude attach`, so it reappears in
        // the strip, which is where the user would look for it.
        if (parentSessionId is not null) PublishLedger.BeginPublish(shortId, parentSessionId, absPath);
        try { return await WatchAsync(cwd, absPath, configDir, shortId, ct); }
        finally { PublishLedger.EndPublish(shortId); }
    }

    private static async Task<ArtifactPublishOutcome> WatchAsync(string cwd, string absPath, string? configDir,
        string shortId, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + Timeout;
        string? sessionId = null, state = null, sessionCwd = null;
        int idlePolls = 0;
        try
        {
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(PollEvery, ct);
                var agents = await RunClaudeAsync(cwd, configDir, "agents --json --all", waitForExit: true, ct);
                if (ParseAgent(agents, shortId) is { } a)
                    (sessionId, state, sessionCwd) = (a.SessionId ?? sessionId, a.State, a.Cwd ?? sessionCwd);

                // The transcript lives under the project folder of wherever the session actually runs, which is a
                // worktree when the CLI isolated it there, not necessarily the folder it was started in.
                if (sessionId is not null && TranscriptPath(sessionId, sessionCwd ?? cwd, configDir) is { } transcript
                    && FindPublished(transcript, absPath) is { } published)
                {
                    await CleanUpAsync(cwd, configDir, shortId);
                    return new(published, null);
                }

                // Still busy → keep waiting. Anything else (done, or waiting on something) twice in a row with no
                // result means it isn't coming.
                idlePolls = state is null or "working" or "starting" ? 0 : idlePolls + 1;
                if (idlePolls >= 2)
                    return new(null, $"the background session ended ({state}) without publishing", shortId);
            }
            return new(null, "timed out waiting for the artifact", shortId);
        }
        catch (OperationCanceledException) { await CleanUpAsync(cwd, configDir, shortId); throw; }
        catch (Exception ex) { return new(null, ex.Message, shortId); }
    }

    /// <summary>The settings a publishing session runs with: <c>worktree.bgIsolation = none</c>. In a git repo a background
    /// session moves into its own worktree before its first edit, and a worktree lacks the new, untracked file being
    /// published, which only invites the session to copy it there. The publish edits nothing, so it stays in place.</summary>
    internal const string NoWorktreeSettingsJson = """{ "worktree": { "bgIsolation": "none" } }""";

    // Written to Perch's data folder and passed as a path: a file, rather than inline JSON, keeps quotes off the
    // cmd / sh command line. Null when it can't be written (or its path couldn't ride the line); the publish then
    // runs without it, which the worktree-tolerant transcript lookup still copes with.
    private static string? NoWorktreeSettings()
    {
        try
        {
            var path = System.IO.Path.Combine(PerchSessionPlugin.DataDir, "bg-publish-settings.json");
            PerchSessionPlugin.Write(path, NoWorktreeSettingsJson);
            return ClaudeCli.IsSafePrompt(path) ? path : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static async Task CleanUpAsync(string cwd, string? configDir, string shortId)
    {
        try
        {
            await RunClaudeAsync(cwd, configDir, $"stop {shortId}", waitForExit: true, CancellationToken.None);
            await RunClaudeAsync(cwd, configDir, $"rm {shortId}", waitForExit: true, CancellationToken.None);
        }
        catch { /* best-effort: a leftover idle background session is harmless */ }
    }

    // `claude <args>` in cwd under configDir, stdout+stderr captured. --bg forks a background service that can keep
    // the inherited pipes open, so its start isn't waited out to EOF: output is read until the id line shows up (or
    // the process exits).
    private static async Task<string> RunClaudeAsync(string cwd, string? configDir, string args, bool waitForExit,
        CancellationToken ct)
    {
        var psi = ClaudeCli.CreateStartInfo(args);
        psi.WorkingDirectory = cwd;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.StandardOutputEncoding = System.Text.Encoding.UTF8;
        foreach (var name in ScrubbedEnv) psi.Environment.Remove(name);
        if (!string.IsNullOrWhiteSpace(configDir)) psi.Environment["CLAUDE_CONFIG_DIR"] = configDir;

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("couldn't start claude");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        var err = proc.StandardError.ReadToEndAsync(timeout.Token);
        var output = new System.Text.StringBuilder();
        try
        {
            while (await proc.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
            {
                output.AppendLine(line);
                if (!waitForExit && ParseBackgroundId(line) is not null) return output.ToString();
            }
            await proc.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Never the tree for a --bg start: that would take the background service with it.
            try { proc.Kill(entireProcessTree: waitForExit); } catch { }
            throw new TimeoutException($"claude {args.Split(' ')[0]} didn't answer");
        }
        if (err.IsCompletedSuccessfully) output.Append(err.Result);
        return output.ToString();
    }

    private static string Tail(string output)
    {
        var t = output.Trim();
        return t.Length == 0 ? "" : ": " + (t.Length > 200 ? t[^200..] : t);
    }

    /// <summary>The short id from <c>claude --bg</c>'s <c>backgrounded · 715e4caa</c> line, else null.</summary>
    internal static string? ParseBackgroundId(string output) =>
        Regex.Match(output, @"backgrounded\W+([0-9a-f]{6,})", RegexOptions.IgnoreCase) is { Success: true } m
            ? m.Groups[1].Value : null;

    /// <summary>The session id, state and working directory of background session <paramref name="shortId"/> in
    /// <c>claude agents --json --all</c> output, else null.</summary>
    internal static (string? SessionId, string? State, string? Cwd)? ParseAgent(string json, string shortId)
    {
        try
        {
            int start = json.IndexOf('[');
            if (start < 0) return null;
            using var doc = JsonDocument.Parse(json[start..]);
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                if (Str(el, "id") != shortId) continue;
                return (Str(el, "sessionId"), Str(el, "state"), Str(el, "cwd"));
            }
        }
        catch (JsonException) { }
        return null;
    }

    private static string? TranscriptPath(string sessionId, string cwd, string? configDir)
    {
        if (!ClaudeCli.IsSessionId(sessionId)) return null;
        if (!string.IsNullOrWhiteSpace(configDir))
        {
            var direct = System.IO.Path.Combine(configDir, "projects", TranscriptLocator.EncodeProjectDir(cwd), sessionId + ".jsonl");
            if (File.Exists(direct)) return direct;
        }
        return TranscriptLocator.Resolve(sessionId, cwd);
    }

    /// <summary>The last artifact the transcript at <paramref name="transcriptPath"/> published for
    /// <paramref name="absPath"/>, else the last it published at all, else null. The background session exists only for
    /// this one publish, so a result recorded under another path (a copy it made inside a worktree, despite being told
    /// not to) is still this file's. Tolerates a partial last line.</summary>
    internal static PublishedArtifact? FindPublished(string transcriptPath, string absPath)
    {
        PublishedArtifact? exact = null, any = null;
        try
        {
            using var fs = new FileStream(transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs);
            while (reader.ReadLine() is { } line)
            {
                if (ParseRecord(line) is not { } a) continue;
                any = a;
                if (SamePath(a.Path, absPath)) exact = a;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return exact ?? any;
    }

    /// <summary>The artifact one transcript line records, when it's an <c>Artifact</c> tool result with a claude.ai
    /// URL (<c>toolUseResult.url</c>), else null.</summary>
    internal static PublishedArtifact? ParseRecord(string line)
    {
        if (!line.Contains("\"toolUseResult\"", StringComparison.Ordinal) ||
            !line.Contains("claude.ai/artifact/", StringComparison.Ordinal)) return null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            if (!doc.RootElement.TryGetProperty("toolUseResult", out var r) || r.ValueKind != JsonValueKind.Object) return null;
            if (Str(r, "url") is not { } url || !url.StartsWith("https://claude.ai/artifact/", StringComparison.Ordinal))
                return null;
            bool updated = r.TryGetProperty("updated", out var u) && u.ValueKind == JsonValueKind.True;
            return new PublishedArtifact(url, Str(r, "path") ?? "", Str(r, "title"), updated);
        }
        catch (JsonException) { return null; }
    }

    private static bool SamePath(string recorded, string absPath)
    {
        if (recorded.Length == 0) return true;
        try
        {
            return string.Equals(System.IO.Path.GetFullPath(recorded), System.IO.Path.GetFullPath(absPath),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    private static string? Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

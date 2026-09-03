using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Perch.Data.Control;

/// <summary>
/// Spawns and drives a Claude Code session over the CLI's bidirectional stream-json interface
/// (<c>claude -p --input-format stream-json --output-format stream-json --permission-prompt-tool stdio</c>)
/// — the same wire protocol the official Agent SDKs wrap. Perch owns the process: it sends user prompts,
/// answers permission requests, switches permission mode and interrupts, and decodes the output stream
/// into <see cref="SessionEvent"/>s via <see cref="StreamJsonParser"/>.
///
/// <para>Threading: <see cref="EventReceived"/> and <see cref="Exited"/> are raised on the background
/// pump thread — UI consumers marshal via their dispatcher. Writers are serialised by a lock.</para>
///
/// <para>PoC status: shapes verified live against claude 2.1.247 except <c>interrupt</c> (implemented per
/// the Agent SDK's protocol but not yet exercised); see <c>docs/session-control-poc.md</c>.</para>
/// </summary>
internal sealed class ClaudeSessionController : IDisposable
{
    /// <summary>A decoded output event. Raised on the pump thread.</summary>
    public event Action<SessionEvent>? EventReceived;

    /// <summary>The process ended: exit code + a clipped stderr tail (empty when clean).</summary>
    public event Action<int, string>? Exited;

    private Process? _process;
    private StreamWriter? _stdin;
    private readonly Lock _writeLock = new();
    private int _requestCounter;

    /// <summary>The session id. Known from launch (pinned via <c>--session-id</c> for a new session, or the
    /// <c>--resume</c> id), then confirmed by <c>system/init</c>.</summary>
    public string? SessionId { get; private set; }
    public string Cwd { get; private set; } = "";
    public bool IsRunning => _process is { HasExited: false };

    /// <summary>Launches the session in <paramref name="cwd"/>. Model/mode may be null for the user's
    /// defaults; both are validated against known tokens since they end up on a shell command line.
    /// <paramref name="resumeSessionId"/> resumes an existing session by id (<c>--resume</c>) — the
    /// conversation continues under the <em>same</em> id and transcript, the mechanism behind "elevate a
    /// terminal session into Perch" (session-control M4). Otherwise a fresh GUID is pinned via
    /// <c>--session-id</c> (or <paramref name="newSessionId"/> when the caller chose one), so the id — and
    /// with it the <see cref="ControlledSessions"/> registration and the <see cref="SessionLock"/> sidecar —
    /// exist <em>before</em> the CLI's <c>init</c>, closing the pre-init window of the collision defences.</summary>
    public void Start(string cwd, string? model = null, string? permissionMode = null, string? resumeSessionId = null,
        string? newSessionId = null, string? effort = null)
    {
        if (IsRunning) throw new InvalidOperationException("Session already running.");

        bool resume = IsSafeToken(resumeSessionId);
        var id = resume ? resumeSessionId! : (IsSafeToken(newSessionId) ? newSessionId! : Guid.NewGuid().ToString());

        var args = "-p --input-format stream-json --output-format stream-json --verbose" +
                   " --include-partial-messages --permission-prompt-tool stdio";
        args += resume ? $" --resume {id}" : $" --session-id {id}";
        if (IsSafeToken(model)) args += $" --model {model}";
        if (IsSafeToken(permissionMode) && permissionMode != "default") args += $" --permission-mode {permissionMode}";
        if (IsEffortLevel(effort)) args += $" --effort {effort}";

        // `claude` is a .cmd shim on Windows PATH, so it needs a shell host (same reason
        // SessionLauncher.Reopen never execs it directly). Elsewhere it's a plain executable.
        var psi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo { FileName = "cmd.exe", Arguments = $"/d /s /c \"claude {args}\"" }
            : new ProcessStartInfo { FileName = "/bin/sh", Arguments = $"-lc \"claude {args}\"" };
        psi.WorkingDirectory = cwd;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardInput = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.StandardOutputEncoding = System.Text.Encoding.UTF8;
        psi.StandardErrorEncoding = System.Text.Encoding.UTF8;
        psi.StandardInputEncoding = new System.Text.UTF8Encoding(false);
        // Lets perch-hook (a child of this claude) tell "Perch's own controlled session starting" from "a
        // normal claude opened a Perch-controlled id" — see SessionLock / collision defence (c).
        psi.Environment[SessionLock.OwnerEnvVar] = Environment.ProcessId.ToString();

        // Ownership is claimed before the process exists so nothing can race the pre-init window; a
        // failed launch releases it again below.
        SessionId = id;
        Cwd = cwd;
        ControlledSessions.Register(id);
        SessionLock.Acquire(id, cwd);

        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start claude.");
        }
        catch
        {
            ControlledSessions.Unregister(id);
            SessionLock.Release(id);
            SessionId = null;
            throw;
        }
        _process = process;
        _stdin = process.StandardInput;

        // The control-protocol handshake; the CLI answers with its capability catalogue (ignored here).
        WriteLine(new JsonObject
        {
            ["type"] = "control_request",
            ["request_id"] = NextRequestId(),
            ["request"] = new JsonObject { ["subtype"] = "initialize" },
        });

        Task.Run(() => Pump(process));
    }

    // One dedicated reader per pipe (stdout pumped line-by-line, stderr drained async) so neither can
    // back-pressure the child into a deadlock.
    private void Pump(Process process)
    {
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            while (process.StandardOutput.ReadLine() is { } line)
            {
                foreach (var ev in StreamJsonParser.Parse(line))
                {
                    // Normally confirms the pinned/resumed id; if the CLI reports a different one (it shouldn't),
                    // follow it so ownership tracks the transcript that is actually being written.
                    if (ev is SessionInitEvent init && init.SessionId.Length > 0 && init.SessionId != SessionId)
                    {
                        ControlledSessions.Unregister(SessionId);
                        SessionLock.Release(SessionId);
                        SessionId = init.SessionId;
                        ControlledSessions.Register(init.SessionId);   // the valet + focus routing skip owned sessions
                        SessionLock.Acquire(init.SessionId, Cwd);
                    }
                    EventReceived?.Invoke(ev);
                }
            }
        }
        catch
        {
            // Stream torn down mid-read (Stop/Dispose); fall through to the exit notification.
        }

        int exitCode = -1;
        string errTail = "";
        try
        {
            process.WaitForExit(5000);
            exitCode = process.HasExited ? process.ExitCode : -1;
            var err = stderr.IsCompletedSuccessfully ? stderr.Result : "";
            errTail = ToolSummary.Clip(err.Length > 400 ? err[^400..] : err);
        }
        catch { /* best effort */ }
        ControlledSessions.Unregister(SessionId);
        SessionLock.Release(SessionId);
        Exited?.Invoke(exitCode, errTail);
    }

    /// <summary>Queues a user prompt; the CLI runs a full agentic turn per prompt.</summary>
    public void SendPrompt(string text) => WriteLine(new JsonObject
    {
        ["type"] = "user",
        ["message"] = new JsonObject
        {
            ["role"] = "user",
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        },
    });

    /// <summary>Answers a <see cref="PermissionRequestEvent"/>. On allow the tool's input is echoed back
    /// as <c>updatedInput</c> — unchanged, or <paramref name="updatedInput"/> when the caller edited it (how
    /// <c>AskUserQuestion</c> is answered: the input plus an <c>answers</c> object, see
    /// <see cref="AskUserQuestionInput"/>).</summary>
    public void RespondToPermission(PermissionRequestEvent request, bool allow, string? denyMessage = null, JsonNode? updatedInput = null)
    {
        JsonObject verdict;
        if (allow)
        {
            JsonNode? input = updatedInput;
            if (input is null)
                try { input = JsonNode.Parse(request.InputJson); } catch { /* omit on parse failure */ }
            verdict = new JsonObject { ["behavior"] = "allow", ["updatedInput"] = input ?? new JsonObject() };
        }
        else
        {
            verdict = new JsonObject { ["behavior"] = "deny", ["message"] = denyMessage ?? "Denied by the user in Perch." };
        }
        WriteLine(new JsonObject
        {
            ["type"] = "control_response",
            ["response"] = new JsonObject
            {
                ["subtype"] = "success",
                ["request_id"] = request.RequestId,
                ["response"] = verdict,
            },
        });
    }

    /// <summary>Switches the live session's permission mode; acked as a <see cref="ModeChangedEvent"/>.</summary>
    public void SetPermissionMode(string mode)
    {
        if (!IsSafeToken(mode)) return;
        WriteLine(new JsonObject
        {
            ["type"] = "control_request",
            ["request_id"] = NextRequestId(),
            ["request"] = new JsonObject { ["subtype"] = "set_permission_mode", ["mode"] = mode },
        });
    }

    /// <summary>Switches the model mid-session — the Agent SDK's <c>setModel</c> control request.</summary>
    public void SetModel(string model)
    {
        if (!IsSafeToken(model)) return;
        WriteLine(new JsonObject
        {
            ["type"] = "control_request",
            ["request_id"] = NextRequestId(),
            ["request"] = new JsonObject { ["subtype"] = "set_model", ["model"] = model },
        });
    }

    /// <summary>Changes the effort level mid-session by sending the documented <c>/effort &lt;level&gt;</c> command
    /// as a user message (slash commands are accepted over stream-json input in print mode).</summary>
    public void SetEffort(string level)
    {
        if (!IsEffortLevel(level) && level != "auto") return;   // "auto" clears the saved level
        SendPrompt($"/effort {level}");
    }

    /// <summary>The CLI's accepted effort levels (<c>--effort</c> / <c>/effort</c>).</summary>
    public static readonly string[] EffortLevels = ["low", "medium", "high", "xhigh", "max"];

    public static bool IsEffortLevel(string? s) => s is not null && Array.IndexOf(EffortLevels, s) >= 0;

    /// <summary>Interrupts the in-flight turn (the stream-json equivalent of Esc).</summary>
    public void Interrupt() => WriteLine(new JsonObject
    {
        ["type"] = "control_request",
        ["request_id"] = NextRequestId(),
        ["request"] = new JsonObject { ["subtype"] = "interrupt" },
    });

    /// <summary>Ends the session: closing stdin lets the CLI finish and exit; a process that lingers is
    /// killed with its tree. The session remains resumable later via <c>claude --resume</c>.</summary>
    public void Stop()
    {
        var process = _process;
        lock (_writeLock)
        {
            try { _stdin?.Close(); } catch { /* already gone */ }
            _stdin = null;
        }
        if (process is null) return;
        Task.Run(() =>
        {
            try
            {
                if (!process.WaitForExit(3000)) process.Kill(entireProcessTree: true);
            }
            catch { /* already exited */ }
        });
    }

    public void Dispose() => Stop();

    private void WriteLine(JsonObject payload)
    {
        lock (_writeLock)
        {
            if (_stdin is null) return;
            try
            {
                _stdin.WriteLine(payload.ToJsonString());
                _stdin.Flush();
            }
            catch { /* pipe closed under us; the Exited event reports the death */ }
        }
    }

    private string NextRequestId() => $"perch_req_{Interlocked.Increment(ref _requestCounter)}";

    // Values interpolated into the shell command line are restricted to bare identifier-ish tokens
    // (model names, permission modes) — never free text.
    private static bool IsSafeToken(string? s) =>
        !string.IsNullOrEmpty(s) && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_');
}

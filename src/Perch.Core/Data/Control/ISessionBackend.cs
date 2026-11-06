using System.Text.Json.Nodes;

namespace Perch.Data.Control;

/// <summary>
/// What a session view drives: the <c>claude</c> process on this machine (<see cref="ClaudeSessionController"/>), or a
/// session another Perch hosts, reached over the network (docs/remote-workers-plan.md). Both feed the same
/// <see cref="SessionEvent"/>s into a <see cref="SessionConversation"/>, so the session window doesn't care which.
/// <para>Events are raised off the UI thread; consumers marshal.</para>
/// </summary>
internal interface ISessionBackend : IDisposable
{
    event Action<SessionEvent>? EventReceived;

    /// <summary>The session ended: exit code + a clipped stderr tail (empty when clean).</summary>
    event Action<int, string>? Exited;

    string? SessionId { get; }
    bool IsRunning { get; }

    /// <summary>The session runs on another machine. Anything that touches this machine's files or process (rewind,
    /// restart, hand-back, Remote Control) isn't available.</summary>
    bool IsRemote { get; }

    /// <summary>Queues a prompt. <paramref name="uuid"/> names it on the wire, so the CLI's echo of it can be matched
    /// on every view; one is made up when null.</summary>
    void SendPrompt(string text, IReadOnlyList<ImageContent>? images, string? uuid = null);

    void RespondToPermission(PermissionRequestEvent request, bool allow, string? denyMessage = null, JsonNode? updatedInput = null);
    void SetPermissionMode(string mode);
    void SetModel(string model);
    void SetEffort(string level);
    void Interrupt();
    void StopTask(string taskId);
    void Stop();
}

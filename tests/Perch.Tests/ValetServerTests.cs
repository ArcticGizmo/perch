using System.IO.Pipes;
using System.Text;
using Perch.Data.Control;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// The permission-valet pipe protocol (session-control M2, docs/session-control-plan.md): a real
/// named-pipe round-trip against <see cref="ValetServer"/> the way <c>perch-hook valet</c> drives it —
/// one request line, one reply line per connection.
/// </summary>
public class ValetServerTests
{
    private const string SamplePayload =
        """{"session_id":"sess-1","transcript_path":"C:\\t.jsonl","cwd":"C:\\proj","hook_event_name":"PreToolUse","tool_name":"Write","tool_input":{"file_path":"C:\\proj\\a.txt","content":"hi"},"permission_mode":"default"}""";

    private static string UniquePipe() => "perch-valet-test-" + Guid.NewGuid().ToString("N");

    private static async Task<string> RoundTrip(ValetServer server, string pipeName, string payload)
    {
        server.Start();
        await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(5000);
        var bytes = Encoding.UTF8.GetBytes(payload + "\n");
        await client.WriteAsync(bytes);
        await client.FlushAsync();
        using var reader = new StreamReader(client, Encoding.UTF8, leaveOpen: true);
        var reply = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
        return reply ?? "";
    }

    [Fact]
    public async Task AllowDecision_RoundTrips()
    {
        var pipe = UniquePipe();
        ValetRequest? seen = null;
        using var server = new ValetServer(pipe)
        {
            Decide = r => { seen = r; return Task.FromResult(ValetDecision.Allowed("looks safe")); },
        };

        var reply = await RoundTrip(server, pipe, SamplePayload);

        Assert.Contains("\"decision\":\"allow\"", reply);
        Assert.Contains("looks safe", reply);
        Assert.NotNull(seen);
        Assert.Equal("sess-1", seen!.SessionId);
        Assert.Equal("Write", seen.ToolName);
        Assert.Equal(@"C:\proj", seen.Cwd);
        Assert.Equal("default", seen.PermissionMode);
        Assert.Contains("a.txt", seen.ToolInput?.ToJsonString());
    }

    [Fact]
    public async Task NoDecider_AnswersPass()
    {
        var pipe = UniquePipe();
        using var server = new ValetServer(pipe);
        var reply = await RoundTrip(server, pipe, SamplePayload);
        Assert.Contains("\"decision\":\"pass\"", reply);
    }

    [Fact]
    public async Task UnparseableRequest_AnswersPass()
    {
        var pipe = UniquePipe();
        using var server = new ValetServer(pipe)
        {
            Decide = _ => Task.FromResult(ValetDecision.Denied()),   // must never be consulted
        };
        var reply = await RoundTrip(server, pipe, "not json at all");
        Assert.Contains("\"decision\":\"pass\"", reply);
    }

    [Fact]
    public async Task DeciderThrow_FailsOpenAsPass()
    {
        var pipe = UniquePipe();
        using var server = new ValetServer(pipe) { Decide = _ => throw new InvalidOperationException("boom") };
        var reply = await RoundTrip(server, pipe, SamplePayload);
        Assert.Contains("\"decision\":\"pass\"", reply);
    }

    [Fact]
    public void ReadOnlyTools_PassHeuristic()
    {
        Assert.True(ValetProtocol.IsReadOnlyTool("Read"));
        Assert.True(ValetProtocol.IsReadOnlyTool("Grep"));
        Assert.True(ValetProtocol.IsReadOnlyTool("Task"));
        Assert.False(ValetProtocol.IsReadOnlyTool("Bash"));
        Assert.False(ValetProtocol.IsReadOnlyTool("PowerShell"));
        Assert.False(ValetProtocol.IsReadOnlyTool("Write"));
        Assert.False(ValetProtocol.IsReadOnlyTool("Edit"));
        Assert.False(ValetProtocol.IsReadOnlyTool("SomeMcpTool"));
    }

    [Fact]
    public void ControlledSessions_RegisterOwnUnregister()
    {
        ControlledSessions.Register("valet-test-owned");
        Assert.True(ControlledSessions.Owns("valet-test-owned"));
        ControlledSessions.Unregister("valet-test-owned");
        Assert.False(ControlledSessions.Owns("valet-test-owned"));
        Assert.False(ControlledSessions.Owns(null));
    }
}

using Perch.Data.Control;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// The <c>perch</c> CLI's claude-shaped argument parsing and its pipe wire format (docs/session-ui-plan.md,
/// Phase 3): which launches count as "open a session", and that an intent survives the JSON round trip.
/// </summary>
public class ControlProtocolTests
{
    private static readonly string Here = AppContext.BaseDirectory.TrimEnd('\\', '/');

    [Fact]
    public void NoSessionArgs_IsNotAnIntent()
    {
        Assert.Null(SessionOpenIntent.FromArgs([], Here));
        Assert.Null(SessionOpenIntent.FromArgs(["--autostarted"], Here));
        Assert.Null(SessionOpenIntent.FromArgs(["render", "out"], Here));
        Assert.Null(SessionOpenIntent.FromArgs([@"C:\definitely\not\a\real\dir\xyz"], Here));
    }

    [Fact]
    public void ResumeWithId()
    {
        var i = SessionOpenIntent.FromArgs(["--resume", "5b4d131d-dfd4-4103-860c-f5c96094b598"], Here);
        Assert.NotNull(i);
        Assert.Equal("5b4d131d-dfd4-4103-860c-f5c96094b598", i!.ResumeId);
        Assert.False(i.PickResume);
        Assert.Equal(Here, i.Cwd);
    }

    [Fact]
    public void BareResume_AsksForThePicker_AndKeepsAFollowingDir()
    {
        var i = SessionOpenIntent.FromArgs(["--resume", Here], Here);
        Assert.NotNull(i);
        Assert.Null(i!.ResumeId);
        Assert.True(i.PickResume);
        Assert.Equal(Path.GetFullPath(Here), i.Cwd);

        var bare = SessionOpenIntent.FromArgs(["-r"], @"C:\somewhere");
        Assert.True(bare!.PickResume);
        Assert.Equal(@"C:\somewhere", bare.Cwd);
    }

    [Fact]
    public void ContinueAndOptions()
    {
        var i = SessionOpenIntent.FromArgs(["-c", "--model", "opus", "--permission-mode", "plan"], Here);
        Assert.NotNull(i);
        Assert.True(i!.Continue);
        Assert.Equal("opus", i.Model);
        Assert.Equal("plan", i.PermissionMode);
    }

    [Fact]
    public void PositionalDir_StartsFreshThere()
    {
        var i = SessionOpenIntent.FromArgs([Here], @"C:\elsewhere");
        Assert.NotNull(i);
        Assert.Equal(Path.GetFullPath(Here), i!.Cwd);
        Assert.Null(i.ResumeId);
        Assert.False(i.Continue);
        Assert.False(i.PickResume);
    }

    [Fact]
    public void Json_RoundTrips()
    {
        var intent = new SessionOpenIntent(@"C:\proj", "abc12345-id", PickResume: false, Continue: false, Model: "sonnet", PermissionMode: "acceptEdits");
        var back = SessionOpenIntent.Parse(intent.ToJson());
        Assert.Equal(intent, back);

        var pick = new SessionOpenIntent(@"C:\proj", PickResume: true);
        Assert.Equal(pick, SessionOpenIntent.Parse(pick.ToJson()));

        Assert.Null(SessionOpenIntent.Parse("nope"));
        Assert.Null(SessionOpenIntent.Parse("{\"resume\":\"x\"}"));   // no cwd → unusable

        var reply = new ControlReply(true, "opened");
        Assert.Equal(reply, ControlReply.Parse(reply.ToJson()));
    }
}

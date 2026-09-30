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
        // FromArgs never invents a session for a bare or tray-only launch: `--autostarted` (the hook) and
        // `--tray` are ignored as unknown flags. A bare `perch` becomes "start fresh here" only via Program's
        // interactive-terminal check + StartFresh below — not from parsing.
        Assert.Null(SessionOpenIntent.FromArgs([], Here));
        Assert.Null(SessionOpenIntent.FromArgs(["--autostarted"], Here));
        Assert.Null(SessionOpenIntent.FromArgs(["--tray"], Here));
        Assert.Null(SessionOpenIntent.FromArgs(["render", "out"], Here));
        Assert.Null(SessionOpenIntent.FromArgs([@"C:\definitely\not\a\real\dir\xyz"], Here));
    }

    [Fact]
    public void StartFresh_IsACwdOnlyIntent()
    {
        var i = SessionOpenIntent.StartFresh(Here);
        Assert.Equal(Here, i.Cwd);
        Assert.Null(i.ResumeId);
        Assert.False(i.Continue);
        Assert.False(i.PickResume);
        Assert.Null(i.Model);
        // Round-trips over the pipe like any other intent (a running tray receives it as "open a session here").
        Assert.Equal(i, SessionOpenIntent.Parse(i.ToJson()));
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
        var intent = new SessionOpenIntent(Here, "abc12345-id", PickResume: false, Continue: false, Model: "sonnet", PermissionMode: "acceptEdits");
        var back = SessionOpenIntent.Parse(intent.ToJson());
        Assert.Equal(intent, back);

        var pick = new SessionOpenIntent(Here, PickResume: true);
        Assert.Equal(pick, SessionOpenIntent.Parse(pick.ToJson()));

        // The launch-monitor hint (the terminal's monitor) survives the round trip, so the tray places the
        // window where the CLI process sampled it.
        var withMon = new SessionOpenIntent(Here,
            OriginMonitor: new Perch.Platform.MonitorGeometry(-1920, 0, 1920, 1080, -1920, 0, 1920, 1040, 1.5));
        Assert.Equal(withMon, SessionOpenIntent.Parse(withMon.ToJson()));

        Assert.Null(SessionOpenIntent.Parse("nope"));
        Assert.Null(SessionOpenIntent.Parse("{\"resume\":\"x\"}"));   // no cwd → unusable

        var reply = new ControlReply(true, "opened");
        Assert.Equal(reply, ControlReply.Parse(reply.ToJson()));
    }
    // ---- review fixes CP13: an intent from the pipe or the handoff file is validated like a command line ----

    [Theory]
    [InlineData("{\"cwd\":\"C:\\\\definitely\\\\not\\\\a\\\\real\\\\dir\\\\xyz\"}")]   // no such folder
    [InlineData("{\"cwd\":\"relative\\\\dir\"}")]                                     // not absolute
    [InlineData("{\"cwd\":\"@HERE@\",\"resume\":\"x\"}")]                            // too short for an id
    [InlineData("{\"cwd\":\"@HERE@\",\"resume\":\"abcdefgh & calc\"}")]              // not an id at all
    [InlineData("{\"cwd\":\"@HERE@\",\"mode\":\"yolo\"}")]                           // an unknown mode
    [InlineData("{\"cwd\":\"@HERE@\",\"mode\":\"plan --dangerously-skip-permissions\"}")]
    [InlineData("{\"cwd\":\"@HERE@\",\"model\":\"opus; rm -rf\"}")]                  // not a token
    public void Parse_RejectsAnIntentWithAnyInvalidField(string json)
    {
        Assert.Null(SessionOpenIntent.Parse(json.Replace("@HERE@", Here.Replace("\\", "\\\\"))));
    }

    [Theory]
    [InlineData("default")]
    [InlineData("auto")]
    [InlineData("plan")]
    [InlineData("acceptEdits")]
    [InlineData("bypassPermissions")]   // allowed: the user's own `perch --permission-mode bypassPermissions`
    public void Parse_AcceptsEveryKnownPermissionMode(string mode)
    {
        var intent = new SessionOpenIntent(Here, PermissionMode: mode);
        Assert.Equal(intent, SessionOpenIntent.Parse(intent.ToJson()));
    }

    [Fact]
    public void FromArgs_IgnoresAnUnknownPermissionMode()
    {
        var i = SessionOpenIntent.FromArgs(["-c", "--permission-mode", "yolo"], Here);
        Assert.NotNull(i);                       // -c still asks for a session
        Assert.Null(i!.PermissionMode);          // but the unknown mode is dropped, as claude would refuse it
    }

    [Fact]
    public void HandoffFile_IsOnlyPerchsOwnTempFile()
    {
        var mine = SessionOpenIntent.NewHandoffFile();
        Assert.True(SessionOpenIntent.IsHandoffFile(mine));

        var temp = Path.GetTempPath();
        Assert.False(SessionOpenIntent.IsHandoffFile(Path.Combine(temp, "perch-intent-XYZ.json")));
        Assert.False(SessionOpenIntent.IsHandoffFile(Path.Combine(temp, "perch-intent-" + new string('A', 32) + ".json")));
        Assert.False(SessionOpenIntent.IsHandoffFile(Path.Combine(temp, "perch-intent-" + new string('a', 32) + ".txt")));
        Assert.False(SessionOpenIntent.IsHandoffFile(Path.Combine(temp, "sub", Path.GetFileName(mine))));   // not directly in temp
        Assert.False(SessionOpenIntent.IsHandoffFile(Path.Combine(temp, "..", Path.GetFileName(mine))));   // climbs out
        Assert.False(SessionOpenIntent.IsHandoffFile(Path.Combine(Here, Path.GetFileName(mine))));        // right name, wrong folder
        Assert.False(SessionOpenIntent.IsHandoffFile(Path.GetFileName(mine)));                             // relative
        Assert.False(SessionOpenIntent.IsHandoffFile(@"C:\Users\me\Documents\thesis.docx"));
    }
}

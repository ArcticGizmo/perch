using Perch.Data.Control;
using Perch.Data.Work;
using Xunit;

namespace Perch.Tests;

public class WorkCliTests
{
    private sealed class Sandbox : IDisposable
    {
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), $"perch-workcli-{Guid.NewGuid():N}");
        public WorkItemStore Store => new(Dir);
        public string Out => _out.ToString();
        public string Err => _err.ToString();
        private StringWriter _out = new(), _err = new();

        public int Run(params string[] args)
        {
            _out = new StringWriter();
            _err = new StringWriter();
            return WorkCli.Run(args, _out, _err, Store);
        }

        public void Dispose()
        {
            try { Directory.Delete(Dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void New_Then_List_ShowsTheItem_AndHowToUseIt()
    {
        using var s = new Sandbox();
        Assert.Equal(0, s.Run("new", "Payments", "refactor"));
        var id = s.Store.LoadAll().Single().Id;
        Assert.Contains($"PERCH_WORK = \"{id}\"", s.Out);
        Assert.Contains($"perch --work {id}", s.Out);

        Assert.Equal(0, s.Run());
        Assert.Contains("Payments refactor", s.Out);
        Assert.Contains("active", s.Out);
    }

    [Fact]
    public void Add_Log_Done_GoThroughTheFile()
    {
        using var s = new Sandbox();
        s.Run("new", "macOS port");
        const string sid = "4f1c0000-aaaa-bbbb-cccc-000000000001";

        Assert.Equal(0, s.Run("add", "macos", sid));        // a name prefix is enough
        Assert.Equal(0, s.Run("add", "macOS port", sid));   // and a name with a space
        Assert.Contains("Already in", s.Out);
        Assert.Equal(0, s.Run("log", "macos", "Notifications", "spike", "works"));
        Assert.Equal(0, s.Run("done", "macos"));

        var item = s.Store.LoadAll().Single();
        Assert.Equal(WorkItemStatus.Done, item.Status);
        Assert.Equal(WorkJoin.Manual, Assert.Single(item.Members).Via);
        Assert.Contains(item.Log, e => !e.ByPerch && e.Text == "Notifications spike works");
        Assert.Contains(item.Log, e => e.ByPerch && e.Text == "Marked done");

        // Done items drop out of the default list but still resolve, so they can be reopened.
        s.Run("list");
        Assert.DoesNotContain("macOS port", s.Out);
        Assert.Equal(0, s.Run("resume", "macos"));
        Assert.Equal(WorkItemStatus.Active, s.Store.LoadAll().Single().Status);
    }

    [Fact]
    public void Failures_AreMessagesAndNonZero()
    {
        using var s = new Sandbox();
        Assert.Equal(1, s.Run("new"));
        Assert.Equal(1, s.Run("log", "nothing-here", "text"));
        Assert.Contains("No work item matches", s.Err);
        s.Run("new", "Real");
        Assert.Equal(1, s.Run("add", "real", "not a session id!"));
        Assert.Equal(1, s.Run("frobnicate"));
        Assert.Contains("perch work help", s.Err);
    }

    [Fact]
    public void Intent_CarriesWorkRef_ThroughArgsAndTheWire()
    {
        var intent = SessionOpenIntent.FromArgs(["--work", "payments"], Path.GetTempPath())!;
        Assert.Equal("payments", intent.WorkRef);

        var back = SessionOpenIntent.Parse(intent.ToJson())!;
        Assert.Equal("payments", back.WorkRef);

        // A ref with control characters can't ride the pipe.
        var bad = (intent with { WorkRef = "pay\nments" }).ToJson();
        Assert.Null(SessionOpenIntent.Parse(bad));
    }

    [Fact]
    public void WorkIsASubcommand_SoItsHelpIsItsOwn()
    {
        Assert.False(CliUsage.IsHelp(["work", "--help"]));
        Assert.Contains("perch work", CliUsage.Help("1.2.3"));
        Assert.Contains("--work", CliUsage.Help("1.2.3"));
    }
}

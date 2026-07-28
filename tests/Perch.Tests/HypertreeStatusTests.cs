using Perch.Data.Hypertree;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// Covers the reader for Hypertree's published <c>status.json</c> — the contract behind the overlay's
/// Hypertree strip. It's another app's file, written live and possibly by a newer version than we know,
/// so the interesting cases are all the ways it can be untrustworthy: a schema we don't understand, a
/// file left behind by a crashed tray, and outright garbage. Every one of them must read as "no
/// Hypertree" rather than throwing or half-parsing.
/// <para>
/// Each test redirects the reader with <c>HYPERTREE_STATE_DIR</c> — Hypertree's own escape hatch, which
/// Perch honours — to a scratch directory, so nothing here touches a real tray's state. Assembly-wide
/// parallelisation is off (see <see cref="TestEnvironment"/>), which is what makes swapping a
/// process-global environment variable safe.
/// </para>
/// </summary>
public class HypertreeStatusTests
{
    private const string DirVar = "HYPERTREE_STATE_DIR";

    // The shape the live tray publishes (captured from Hypertree 0.2.0): main sits in its slot among the
    // branches rather than being a branch itself, and the cursor is an index pair into that array.
    private static string SampleJson(int pid, int schema = 1) => $$"""
    {
      "schema": {{schema}},
      "version": "0.2.0",
      "pid": {{pid}},
      "cli": "C:\\Users\\dev\\AppData\\Local\\Hypertree\\current\\htree.exe",
      "rows": [
        {
          "kind": "branch",
          "id": "57adeeb8-8ca1-4945-a697-87cb9ed48e5c",
          "name": "perch",
          "cursor": 1,
          "desktops": [
            { "id": "8e69b47b-06ac-47c9-99e4-3cb9322af00b", "label": "code" },
            { "id": "1f2d3c4b-5a69-4788-99e4-3cb9322af111", "label": "docs" }
          ]
        },
        {
          "kind": "main",
          "name": "main",
          "cursor": 0,
          "desktops": [
            { "id": "5ebd21ea-f7c9-4cbe-9840-0c8a1f313e48", "label": "1 - Admin" }
          ]
        }
      ],
      "current": { "row": 1, "desktop": 0 }
    }
    """;

    // Runs the body with the reader pointed at a throwaway directory, restoring the variable afterwards
    // so one test can't leak its redirect into the next.
    private static void InScratchDir(Action<string> body)
    {
        var previous = Environment.GetEnvironmentVariable(DirVar);
        var dir = Path.Combine(Path.GetTempPath(), "perch-hypertree-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable(DirVar, dir);
        try { body(dir); }
        finally
        {
            Environment.SetEnvironmentVariable(DirVar, previous);
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    private static void WriteStatus(string dir, string json)
        => File.WriteAllText(Path.Combine(dir, "status.json"), json);

    // The happy path: a live tray's file parses into the rows and cursor the strip draws from. Note the
    // camelCase on the wire — Hypertree serialises with a camelCase policy, so the reader must be
    // case-insensitive or every property comes back empty.
    [Fact]
    public void Read_ParsesLiveStatus()
    {
        InScratchDir(dir =>
        {
            WriteStatus(dir, SampleJson(Environment.ProcessId));

            var status = HypertreeStatusReader.Read();

            Assert.NotNull(status);
            Assert.Equal("0.2.0", status!.Version);
            Assert.EndsWith("htree.exe", status.Cli);
            Assert.Equal(2, status.Rows.Count);

            var branch = status.Rows[0];
            Assert.False(branch.IsMain);
            Assert.Equal("perch", branch.Name);
            Assert.Equal(2, branch.Desktops.Count);

            var main = status.Rows[1];
            Assert.True(main.IsMain);
            Assert.Null(main.Id);

            Assert.Equal(1, status.Current.Row);
            Assert.Equal(0, status.Current.Desktop);
        });
    }

    // The strip marks exactly one row as "here", and it's the one the published cursor points at.
    [Fact]
    public void IsCurrentRow_MarksOnlyTheCursorRow()
    {
        InScratchDir(dir =>
        {
            WriteStatus(dir, SampleJson(Environment.ProcessId));
            var status = HypertreeStatusReader.Read()!;

            Assert.False(status.IsCurrentRow(0));
            Assert.True(status.IsCurrentRow(1));
        });
    }

    // A jump addresses a branch by its stable id and main by the literal "main" — never by list
    // position, which shifts when the user reorders the stack.
    [Fact]
    public void Target_UsesIdForBranchesAndLiteralForMain()
    {
        InScratchDir(dir =>
        {
            WriteStatus(dir, SampleJson(Environment.ProcessId));
            var status = HypertreeStatusReader.Read()!;

            Assert.Equal("57adeeb8-8ca1-4945-a697-87cb9ed48e5c", status.Rows[0].Target);
            Assert.Equal("main", status.Rows[1].Target);
        });
    }

    // Each line trails the desktop a jump would land on — the row's resume point, not its first desktop.
    [Fact]
    public void ResumeLabel_FollowsTheCursor()
    {
        InScratchDir(dir =>
        {
            WriteStatus(dir, SampleJson(Environment.ProcessId));
            var status = HypertreeStatusReader.Read()!;

            Assert.Equal("docs", status.Rows[0].ResumeLabel);      // cursor 1, not "code"
            Assert.Equal("1 - Admin", status.Rows[1].ResumeLabel);
        });
    }

    // An out-of-range cursor (a file written mid-edit, or a contract drift) yields an empty label rather
    // than throwing out of the paint routine.
    [Fact]
    public void ResumeLabel_IsEmptyWhenCursorDoesNotResolve()
    {
        var row = new HypertreeRow { Name = "perch", Cursor = 7 };
        Assert.Equal("", row.ResumeLabel);
    }

    // Clicking a branch line jumps to its resume point, so no desktop is spelled out; picking one from the
    // line's trailing chip addresses it by 1-based position on the row. The row part is untouched either
    // way, so a branch still goes by id and main by the literal.
    [Fact]
    public void Address_AppendsThePickedDesktopByPosition()
    {
        Assert.Equal("main", HypertreeBridge.Address("main", -1));
        Assert.Equal("main/1", HypertreeBridge.Address("main", 0));
        Assert.Equal("main/5", HypertreeBridge.Address("main", 4));

        const string id = "57adeeb8-8ca1-4945-a697-87cb9ed48e5c";
        Assert.Equal(id, HypertreeBridge.Address(id, -1));
        Assert.Equal($"{id}/2", HypertreeBridge.Address(id, 1));
    }

    // A schema we don't know is refused outright. Guessing at an unfamiliar layout would paint a strip
    // whose click targets we can't trust.
    [Fact]
    public void Read_RefusesUnknownSchema()
    {
        InScratchDir(dir =>
        {
            WriteStatus(dir, SampleJson(Environment.ProcessId, schema: 99));
            Assert.Null(HypertreeStatusReader.Read());
        });
    }

    // Hypertree deletes the file on a clean exit, but a kill can't — so a stale file naming a dead
    // process must not leave the overlay showing branches for a tray that has gone.
    [Fact]
    public void Read_RejectsFileFromDeadProcess()
    {
        InScratchDir(dir =>
        {
            // 0 is never a real process id, and the reader rejects non-positive pids outright.
            WriteStatus(dir, SampleJson(pid: 0));
            Assert.Null(HypertreeStatusReader.Read());
        });
    }

    // Truncated or garbage content reads as "nothing to show" — never an exception out of the poll.
    [Fact]
    public void Read_ToleratesMalformedContent()
    {
        InScratchDir(dir =>
        {
            WriteStatus(dir, "{ \"schema\": 1, \"rows\": [ { \"kind\":");
            Assert.Null(HypertreeStatusReader.Read());
        });
    }

    // No file at all is the ordinary case for the overwhelming majority of users: Hypertree isn't
    // installed, or has never run.
    [Fact]
    public void Read_ReturnsNullWhenNoFile()
    {
        InScratchDir(_ => Assert.Null(HypertreeStatusReader.Read()));
    }
}

using Perch.Data;
using Perch.Platform;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// <see cref="ClearedSessions"/>: a <c>/clear</c> gives a running process a new session id, and the conversation it
/// replaced must stay out of the Recent list (and the Roost's dormant panes) while that process runs — otherwise it
/// shows up as a not-running session beside the live one, and a reply resumes it in a second process.
/// </summary>
public class ClearedSessionsTests
{
    private static readonly DateTime T0 = new(2026, 10, 10, 9, 0, 0);

    private static ClaudeSession Live(string pid, string sessionId) =>
        new(pid, sessionId, SessionStatus.Idle, @"C:\p", "p", DateTime.Now);

    // Only the pids in Alive have a process; anything else reads as exited.
    private sealed class FakeProbe : IProcessProbe
    {
        public HashSet<int> Alive { get; } = [];
        public bool IsAlive(int pid) => Alive.Contains(pid);
    }

    [Fact]
    public void A_process_under_a_new_id_marks_its_old_id_cleared()
    {
        var cleared = new ClearedSessions(new FakeProbe());
        Assert.False(cleared.Update([Live("100", "old")], T0));
        Assert.True(cleared.Update([Live("100", "new")], T0.AddSeconds(1)));

        Assert.True(cleared.Contains("old"));
        Assert.False(cleared.Contains("new"));
    }

    [Fact]
    public void A_session_that_simply_ends_is_not_cleared()
    {
        var cleared = new ClearedSessions(new FakeProbe());
        cleared.Update([Live("100", "a"), Live("200", "b")], T0);
        Assert.False(cleared.Update([Live("200", "b")], T0.AddSeconds(1)));

        Assert.False(cleared.Contains("a"));
    }

    [Fact]
    public void The_mark_goes_when_the_clearing_process_ends()
    {
        var cleared = new ClearedSessions(new FakeProbe());
        cleared.Update([Live("100", "old")], T0);
        cleared.Update([Live("100", "new")], T0.AddSeconds(1));
        Assert.True(cleared.Update([], T0.AddSeconds(2)));

        Assert.False(cleared.Contains("old"));
    }

    [Fact]
    public void A_scan_that_misses_a_live_process_keeps_its_mark()
    {
        var probe = new FakeProbe { Alive = { 100 } };
        var cleared = new ClearedSessions(probe);
        cleared.Update([Live("100", "old")], T0);
        cleared.Update([Live("100", "new")], T0.AddSeconds(1));
        Assert.False(cleared.Update([], T0.AddSeconds(2)));   // its session file was mid-rewrite
        cleared.Update([Live("100", "new")], T0.AddSeconds(3));

        Assert.True(cleared.Contains("old"));
    }

    [Fact]
    public void A_clear_straddling_a_missed_scan_is_still_seen()
    {
        var probe = new FakeProbe { Alive = { 100 } };
        var cleared = new ClearedSessions(probe);
        cleared.Update([Live("100", "old")], T0);
        cleared.Update([], T0.AddSeconds(1));                  // the /clear's rewrite caught mid-write
        Assert.True(cleared.Update([Live("100", "new")], T0.AddSeconds(2)));

        Assert.True(cleared.Contains("old"));
    }

    [Fact]
    public void A_pid_missing_past_the_grace_is_forgotten_even_if_recycled()
    {
        var probe = new FakeProbe { Alive = { 100 } };      // the pid now belongs to some unrelated process
        var cleared = new ClearedSessions(probe);
        cleared.Update([Live("100", "old")], T0);
        cleared.Update([Live("100", "new")], T0.AddSeconds(1));
        cleared.Update([], T0.AddSeconds(2));
        Assert.True(cleared.Contains("old"));

        Assert.True(cleared.Update([], T0.AddSeconds(1) + ClearedSessions.MissingGrace));
        Assert.False(cleared.Contains("old"));
    }

    [Fact]
    public void The_mark_goes_when_the_old_conversation_runs_again()
    {
        var cleared = new ClearedSessions(new FakeProbe());
        cleared.Update([Live("100", "old")], T0);
        cleared.Update([Live("100", "new")], T0.AddSeconds(1));
        Assert.True(cleared.Update([Live("100", "new"), Live("300", "old")], T0.AddSeconds(2)));   // resumed in another process

        Assert.False(cleared.Contains("old"));
    }

    [Fact]
    public void Repeated_clears_mark_every_replaced_id()
    {
        var cleared = new ClearedSessions(new FakeProbe());
        cleared.Update([Live("100", "a")], T0);
        cleared.Update([Live("100", "b")], T0.AddSeconds(1));
        cleared.Update([Live("100", "c")], T0.AddSeconds(2));

        Assert.True(cleared.Contains("a"));
        Assert.True(cleared.Contains("b"));
        Assert.Equal(new HashSet<string> { "a", "b" }, cleared.Snapshot());
    }

    [Fact]
    public void Folding_the_same_scan_again_changes_nothing()
    {
        var cleared = new ClearedSessions(new FakeProbe());
        cleared.Update([Live("100", "old")], T0);
        cleared.Update([Live("100", "new")], T0.AddSeconds(1));
        Assert.False(cleared.Update([Live("100", "new")], T0.AddSeconds(1)));   // a re-fold after a Recent build
    }

    [Fact]
    public void Dormant_rows_and_rows_without_ids_are_ignored()
    {
        var cleared = new ClearedSessions(new FakeProbe());
        cleared.Update([Live("100", "a"), Live("", "x"), Live("200", "d1") with { IsDormant = true }], T0);
        cleared.Update([Live("100", "b"), Live("", "y"), Live("200", "d2") with { IsDormant = true }], T0.AddSeconds(1));

        Assert.True(cleared.Contains("a"));
        Assert.False(cleared.Contains("x"));
        Assert.False(cleared.Contains("d1"));
    }
}

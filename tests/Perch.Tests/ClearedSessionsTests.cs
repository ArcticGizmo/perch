using Perch.Data;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// <see cref="ClearedSessions"/>: a <c>/clear</c> gives a running process a new session id, and the conversation it
/// replaced must stay out of the Recent list (and the Roost's dormant panes) while that process runs — otherwise it
/// shows up as a not-running session beside the live one, and a reply resumes it in a second process.
/// </summary>
public class ClearedSessionsTests
{
    private static ClaudeSession Live(string pid, string sessionId) =>
        new(pid, sessionId, SessionStatus.Idle, @"C:\p", "p", DateTime.Now);

    [Fact]
    public void A_process_under_a_new_id_marks_its_old_id_cleared()
    {
        var cleared = new ClearedSessions();
        cleared.Update([Live("100", "old")]);
        cleared.Update([Live("100", "new")]);

        Assert.True(cleared.Contains("old"));
        Assert.False(cleared.Contains("new"));
    }

    [Fact]
    public void A_session_that_simply_ends_is_not_cleared()
    {
        var cleared = new ClearedSessions();
        cleared.Update([Live("100", "a"), Live("200", "b")]);
        cleared.Update([Live("200", "b")]);

        Assert.False(cleared.Contains("a"));
    }

    [Fact]
    public void The_mark_goes_when_the_clearing_process_ends()
    {
        var cleared = new ClearedSessions();
        cleared.Update([Live("100", "old")]);
        cleared.Update([Live("100", "new")]);
        cleared.Update([]);

        Assert.False(cleared.Contains("old"));
    }

    [Fact]
    public void The_mark_goes_when_the_old_conversation_runs_again()
    {
        var cleared = new ClearedSessions();
        cleared.Update([Live("100", "old")]);
        cleared.Update([Live("100", "new")]);
        cleared.Update([Live("100", "new"), Live("300", "old")]);   // resumed in another process

        Assert.False(cleared.Contains("old"));
    }

    [Fact]
    public void Repeated_clears_mark_every_replaced_id()
    {
        var cleared = new ClearedSessions();
        cleared.Update([Live("100", "a")]);
        cleared.Update([Live("100", "b")]);
        cleared.Update([Live("100", "c")]);

        Assert.True(cleared.Contains("a"));
        Assert.True(cleared.Contains("b"));
        Assert.Equal(new HashSet<string> { "a", "b" }, cleared.Snapshot());
    }

    [Fact]
    public void Dormant_rows_and_rows_without_ids_are_ignored()
    {
        var cleared = new ClearedSessions();
        cleared.Update([Live("100", "a"), Live("", "x"), Live("200", "d1") with { IsDormant = true }]);
        cleared.Update([Live("100", "b"), Live("", "y"), Live("200", "d2") with { IsDormant = true }]);

        Assert.True(cleared.Contains("a"));
        Assert.False(cleared.Contains("x"));
        Assert.False(cleared.Contains("d1"));
    }
}

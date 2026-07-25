using Perch.Data;
using Xunit;

namespace Perch.Tests;

public class TranscriptReaderTests
{
    private const string Cwd = TestEnvironment.FixtureCwd;

    [Fact]
    public void LastTurnAwaitingAssistant_TrueWhenSubAgentResultUnanswered()
    {
        var reader = new TranscriptReader();
        // Tail is a user tool_result (a sub-agent's result handed back to the parent) with no assistant
        // reply after it — the parent still owes a turn, so it is not done. A trailing metadata record
        // must not change the verdict.
        Assert.True(reader.LastTurnAwaitingAssistant("sessSubReturned", Cwd));
    }

    [Fact]
    public void LastTurnAwaitingAssistant_FalseWhenParentRepliedAfterSub()
    {
        var reader = new TranscriptReader();
        // Same shape but the parent produced its final assistant text after the sub's result: turn
        // complete, so not awaiting (a genuine completion the "done" alert should fire on).
        Assert.False(reader.LastTurnAwaitingAssistant("sessSubDone", Cwd));
    }

    [Fact]
    public void LastTurnAwaitingAssistant_TrueWhenAssistantEndsOnPendingToolUse()
    {
        var reader = new TranscriptReader();
        // Tail is an assistant record whose last block is a tool_use awaiting its result — mid-turn.
        Assert.True(reader.LastTurnAwaitingAssistant("sessPendingTool", Cwd));
    }

    [Fact]
    public void LastTurnAwaitingAssistant_FalseWhenTranscriptMissing()
    {
        var reader = new TranscriptReader();
        Assert.False(reader.LastTurnAwaitingAssistant("no-such-session", Cwd));
    }

    [Fact]
    public void GetActivity_ReturnsMostRecentToolCallPhrase()
    {
        var reader = new TranscriptReader();
        // The last tool_use in sessA is the Bash "npm test" call; later records carry no tool_use.
        Assert.Equal("Running: npm test", reader.GetActivity("sessA", Cwd));
    }

    [Fact]
    public void GetTitle_ReturnsCustomRenameTitle()
    {
        var reader = new TranscriptReader();
        Assert.Equal("Feature work", reader.GetTitle("sessA", Cwd));
    }

    [Fact]
    public void GetTitle_NullWhenNeverRenamed()
    {
        var reader = new TranscriptReader();
        Assert.Null(reader.GetTitle("sessB", Cwd));
    }

    [Fact]
    public void GetContextFill_UsesModelSwitchWindowAndLatestUsage()
    {
        var reader = new TranscriptReader();
        var (fill, window) = reader.GetContextFill("sessA", Cwd);
        Assert.Equal(ModelContext.ExtendedWindow, window.Tokens);   // "(1M context)" model switch
        Assert.Equal(ContextWindowSource.ModelLine, window.Source);
        Assert.NotNull(fill);
        // latest usage = input 50000 + cache_read 150000 = 200000 over a 1,000,000 window.
        Assert.Equal(0.2, (double)fill!.Value, 3);
    }

    [Fact]
    public void GetContextFill_SystemLocalCommandModelLine_ResolvesTheVariant()
    {
        var reader = new TranscriptReader();
        // How current Claude Code records a /model confirmation: a type:"system" record with
        // subtype:"local_command", the stdout in the record's own top-level "content". Reading only the
        // older user-record shape meant this — the one signal that states the 1M variant outright — went
        // unseen on every modern transcript, and a 1M Opus 5 session read as 200k.
        var (fill, window) = reader.GetContextFill("sessSystemModelLine", Cwd);
        Assert.Equal(ModelContext.ExtendedWindow, window.Tokens);
        Assert.Equal(ContextWindowSource.ModelLine, window.Source);
        Assert.Equal("Opus 5 (1M context)", window.Model);
        // used = 12 + 8000 + 42000 = 50012 over a 1,000,000 window.
        Assert.Equal(0.050012, (double)fill!.Value, 5);
    }

    [Fact]
    public void GetContextFill_KeptModelLine_ResolvesTheVariant()
    {
        var reader = new TranscriptReader();
        // The older user-record shape of the same line, still on disk in existing transcripts. "Kept model
        // as …" is what /model logs when it closes on the model already running — just as authoritative as
        // "Set model to …", and message.model is a bare "claude-opus-5" either way.
        var (fill, window) = reader.GetContextFill("sessKeptModel", Cwd);
        Assert.Equal(ModelContext.ExtendedWindow, window.Tokens);
        Assert.Equal(ContextWindowSource.ModelLine, window.Source);
        Assert.Equal("Opus 5 (1M context)", window.Model);
        // used = 12 + 8000 + 42000 = 50012 over a 1,000,000 window.
        Assert.Equal(0.050012, (double)fill!.Value, 5);
    }

    [Fact]
    public void GetContextFill_PromptTooBigForTheAssumedWindow_RatchetsUp()
    {
        var reader = new TranscriptReader();
        // Nothing names the variant and "claude-sonnet-4-6" is a 200k model by the rules — but this
        // session sent a 255,914-token prompt, which the API would have refused on a 200k window. The
        // observed high-water mark overrides the rules, and survives the compaction that follows it.
        var (fill, window) = reader.GetContextFill("sessOversized", Cwd);
        Assert.Equal(ModelContext.ExtendedWindow, window.Tokens);
        Assert.Equal(ContextWindowSource.Observed, window.Source);
        // Fill still tracks the *latest* turn (post-compaction): 10 + 50000 = 50010 over 1,000,000.
        Assert.Equal(0.05001, (double)fill!.Value, 5);
    }

    [Fact]
    public void GetContextFill_DefaultOpusSession_AssumesOneMWindow()
    {
        var reader = new TranscriptReader();
        // No /model switch and no settings.json "model": the window is resolved from the running
        // message.model ("claude-opus-4-8"). That bare id can't distinguish 200k from 1M, and Opus 4.x
        // is 1M, so it must resolve to the extended window — not fall back to 200k.
        var (fill, window) = reader.GetContextFill("sessOpusDefault", Cwd);
        Assert.Equal(ModelContext.ExtendedWindow, window.Tokens);
        Assert.Equal(ContextWindowSource.ModelId, window.Source);
        Assert.NotNull(fill);
        // used = input 100 + cache_creation 10000 + cache_read 50000 = 60100 over a 1,000,000 window.
        Assert.Equal(0.0601, (double)fill!.Value, 4);
    }

    [Fact]
    public void GetContextFill_CountsCacheCreationAfterModelSwitch()
    {
        var reader = new TranscriptReader();
        // A /model switch resets the prompt cache: the final turn has cache_read 0 with the whole
        // context in cache_creation (34621) + input (10). All three input buckets must be summed, or
        // the fill collapses to ~0 and the pressure glyph under-reports badly.
        var (fill, window) = reader.GetContextFill("sessCtxSwitch", Cwd);
        Assert.Equal(ModelContext.DefaultWindow, window.Tokens);   // "Haiku 4.5" → 200k
        Assert.NotNull(fill);
        // (10 + 0 + 34621) / 200000 = 0.173105
        Assert.Equal(0.1731, (double)fill!.Value, 3);
    }

    [Fact]
    public void GetBurnRate_MeasuresRateOverMostRecentBurst()
    {
        var reader = new TranscriptReader();
        // Turns at 10:05:00 / 10:05:30 / 10:06:00 form one burst (a 5-min gap isolates the 10:00:00
        // turn, which must be excluded). The rate is the fresh tokens (input + output + cache_creation,
        // excluding the cache re-read) of every turn after the burst's first, divided by the burst
        // span: ((5000+1000+4000) + (5000+1000+4000)) / 1 minute = 20000 tokens/min.
        var rate = reader.GetBurnRate("sessBurn", Cwd);
        Assert.NotNull(rate);
        Assert.Equal(20000.0, rate!.Value, 3);
    }

    [Fact]
    public void GetBurnRate_NullWhenLatestTurnFollowsALongGap()
    {
        var reader = new TranscriptReader();
        // Two turns 5 minutes apart: the newest stands alone (a BurstGap-sized gap precedes it), so
        // there's no continuous recent burst to measure a live pace from.
        Assert.Null(reader.GetBurnRate("sessBurnIdle", Cwd));
    }

    [Fact]
    public void GetBurnRate_NullWhenSessionMissing()
    {
        var reader = new TranscriptReader();
        Assert.Null(reader.GetBurnRate("nope-no-such-session", Cwd));
    }

    [Fact]
    public void LastTurnWasBareCommand_TrueWhenLastTurnIsASlashCommand()
    {
        var reader = new TranscriptReader();
        Assert.True(reader.LastTurnWasBareCommand("sessCmd", Cwd));
    }

    [Fact]
    public void LastTurnWasBareCommand_FalseWhenLastTurnIsModelWork()
    {
        var reader = new TranscriptReader();
        Assert.False(reader.LastTurnWasBareCommand("sessA", Cwd));
    }

    [Fact]
    public void LastTurnWasInterrupted_TrueWhenLastTurnIsAUserCancel()
    {
        var reader = new TranscriptReader();
        // The turn ends with the synthetic "[Request interrupted by user]" user record — a deliberate
        // Esc/Ctrl+C, not a completion, so it must not raise "done".
        Assert.True(reader.LastTurnWasInterrupted("sessInterrupted", Cwd));
    }

    [Fact]
    public void LastTurnWasInterrupted_TrueForToolUseInterruptVariant()
    {
        var reader = new TranscriptReader();
        // Cancelling mid-tool-call leaves the "[Request interrupted by user for tool use]" marker; the
        // common prefix means the same detection covers it.
        Assert.True(reader.LastTurnWasInterrupted("sessInterruptedToolUse", Cwd));
    }

    [Fact]
    public void LastTurnWasInterrupted_FalseWhenUserResumedAfterCancel()
    {
        var reader = new TranscriptReader();
        // The user interrupted, then prompted again and the model produced a fresh assistant turn — the
        // interrupt is no longer the latest turn, so a later busy->idle really is a completion.
        Assert.False(reader.LastTurnWasInterrupted("sessInterruptResumed", Cwd));
    }

    [Fact]
    public void LastTurnWasInterrupted_FalseForNormalCompletion()
    {
        var reader = new TranscriptReader();
        // sessA ends with real model work (a tool call), not a cancel.
        Assert.False(reader.LastTurnWasInterrupted("sessA", Cwd));
    }

    [Fact]
    public void LastTurnWasInterrupted_FalseWhenSessionMissing()
    {
        var reader = new TranscriptReader();
        Assert.False(reader.LastTurnWasInterrupted("nope-no-such-session", Cwd));
    }

    [Fact]
    public void GetArtifacts_ReturnsPublishedArtifact()
    {
        var reader = new TranscriptReader();
        var artifacts = reader.GetArtifacts("sessA", Cwd);
        var artifact = Assert.Single(artifacts);
        Assert.Equal("Sales Chart", artifact.Title);
        Assert.Contains("/code/artifact/abc123", artifact.Url);
    }

    [Fact]
    public void GetArtifacts_EmptyWhenNonePublished()
    {
        var reader = new TranscriptReader();
        Assert.Empty(reader.GetArtifacts("sessB", Cwd));
    }

    [Fact]
    public void GetTasks_ReconstructsListInCreationOrderWithLatestStatus()
    {
        var reader = new TranscriptReader();
        var tasks = reader.GetTasks("sessTasks", Cwd);

        Assert.Equal(3, tasks.Count);

        // Creation order is the id order; the latest TaskUpdate per id wins.
        Assert.Equal("Phase 0 — Scaffold", tasks[0].Subject);
        Assert.Equal(TaskState.Completed, tasks[0].State);

        Assert.Equal("Phase 1 — Slash commands", tasks[1].Subject);
        Assert.Equal("Building slash commands", tasks[1].ActiveForm);
        Assert.Equal(TaskState.InProgress, tasks[1].State);

        // Never updated → stays pending. (Records a malformed trailing line + an out-of-range
        // taskId, both of which must be tolerated without affecting the result.)
        Assert.Equal(TaskState.Pending, tasks[2].State);
    }

    [Fact]
    public void GetTasks_CompletedCountAndCurrentTaskReflectProgress()
    {
        var reader = new TranscriptReader();
        var session = new ClaudeSession(
            "1", "sessTasks", SessionStatus.Running, Cwd, "proj", DateTime.Now,
            Tasks: reader.GetTasks("sessTasks", Cwd));

        Assert.Equal(1, session.CompletedTaskCount);
        Assert.Equal(3, session.Tasks.Count);
        Assert.NotNull(session.CurrentTask);
        Assert.Equal("Building slash commands", session.CurrentTask!.ActiveForm);
    }

    [Fact]
    public void GetTasks_EmptyWhenSessionHasNoTasks()
    {
        var reader = new TranscriptReader();
        Assert.Empty(reader.GetTasks("sessB", Cwd));
    }

    [Fact]
    public void GetTasks_EmptyWhenListIsStaleAfterANewerPrompt()
    {
        var reader = new TranscriptReader();
        // The one task completes, then the user prompts again with no new tasks — the checklist has
        // been superseded, so nothing should surface (rather than a lingering "1/1").
        Assert.Empty(reader.GetTasks("sessTasksStale", Cwd));
    }

    [Fact]
    public void GetTasks_ReturnsOnlyTheFreshestBatch()
    {
        var reader = new TranscriptReader();
        // Batch one (3 tasks, all completed) is followed by a new prompt and batch two (2 tasks). Only
        // the second batch surfaces — and its TaskUpdate references the session-monotonic id #4, which
        // must still resolve even though batch one was dropped.
        var tasks = reader.GetTasks("sessTasksBatches", Cwd);

        Assert.Equal(2, tasks.Count);
        Assert.Equal("B2 one", tasks[0].Subject);
        Assert.Equal(TaskState.InProgress, tasks[0].State);
        Assert.Equal("B2 two", tasks[1].Subject);
        Assert.Equal(TaskState.Pending, tasks[1].State);
    }
}

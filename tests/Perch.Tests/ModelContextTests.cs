using Perch.Data;
using Xunit;

namespace Perch.Tests;

public class ModelContextTests
{
    [Fact]
    public void ParseDisplayName_AnsiMarked_ReturnsNameBetweenMarkers()
    {
        // The real "/model" confirmation wraps the display name in ANSI bold markers (ESC[1m … ESC[22m).
        var content = "Set model to [1mSonnet 4.6 (1M context)[22m for this session only";
        Assert.Equal("Sonnet 4.6 (1M context)", ModelContext.ParseDisplayName(content));
    }

    [Fact]
    public void ParseDisplayName_KeptModelAs_IsAlsoAModelLine()
    {
        // Closing /model on the model already running reports "Kept model as …" rather than "Set model
        // to …". It states the current model just as authoritatively, and missing it was why a 1M Opus 5
        // session read as 200k.
        var content = "Kept model as [1mOpus 5 (1M context)[22m";
        Assert.Equal("Opus 5 (1M context)", ModelContext.ParseDisplayName(content));
        Assert.True(ModelContext.LooksLikeModelLine(content));
    }

    [Fact]
    public void ParseDisplayName_PlainText_StripsTrailingClauses()
    {
        Assert.Equal("Opus 4.8", ModelContext.ParseDisplayName("Set model to Opus 4.8 for this session only"));
        Assert.Equal("Opus 5", ModelContext.ParseDisplayName("Kept model as Opus 5 · Draws from usage credits"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("some unrelated terminal output")]
    public void ParseDisplayName_NotAModelLine_ReturnsNull(string? content)
    {
        Assert.Null(ModelContext.ParseDisplayName(content));
    }

    [Theory]
    // Display names: the "(1M context)" marker is decisive.
    [InlineData("Sonnet 4.6 (1M context)", ModelContext.ExtendedWindow)]
    [InlineData("Opus 4.8 (1M context)", ModelContext.ExtendedWindow)]
    [InlineData("Opus 5 (1M context)", ModelContext.ExtendedWindow)]
    // Ids: the "[1m]" marker is decisive.
    [InlineData("claude-opus-4-8[1m]", ModelContext.ExtendedWindow)]
    [InlineData("claude-opus-5[1m]", ModelContext.ExtendedWindow)]
    [InlineData("opus[1m]", ModelContext.ExtendedWindow)]
    // Unmarked: family + generation rule. Opus 4.x+ and Sonnet 5+ are 1M.
    [InlineData("Sonnet 5", ModelContext.ExtendedWindow)]
    [InlineData("claude-sonnet-5", ModelContext.ExtendedWindow)]
    [InlineData("Opus 5", ModelContext.ExtendedWindow)]
    [InlineData("claude-opus-5", ModelContext.ExtendedWindow)]
    [InlineData("Opus 4.8", ModelContext.ExtendedWindow)]
    [InlineData("claude-opus-4-6", ModelContext.ExtendedWindow)]
    [InlineData("Sonnet 4.6", ModelContext.DefaultWindow)]
    [InlineData("claude-sonnet-4-6", ModelContext.DefaultWindow)]
    [InlineData("Haiku 4.5", ModelContext.DefaultWindow)]
    [InlineData("claude-haiku-4-5-20251001", ModelContext.DefaultWindow)]
    // Unreleased generations resolve by rule, with no table to update.
    [InlineData("claude-sonnet-6", ModelContext.ExtendedWindow)]
    [InlineData("Opus 7.2", ModelContext.ExtendedWindow)]
    // Bare aliases mean "the current model of this family".
    [InlineData("sonnet", ModelContext.ExtendedWindow)]
    [InlineData("opus", ModelContext.ExtendedWindow)]
    [InlineData("haiku", ModelContext.DefaultWindow)]
    // Nothing recognisable.
    [InlineData("some-other-llm", ModelContext.DefaultWindow)]
    [InlineData(null, ModelContext.DefaultWindow)]
    [InlineData("", ModelContext.DefaultWindow)]
    public void WindowFor_MapsAnyModelStringToAWindow(string? model, int expected)
    {
        Assert.Equal(expected, ModelContext.WindowFor(model));
    }

    [Fact]
    public void Resolve_ModelLineBeatsModelId()
    {
        // The /model line is the only signal that spells the variant out, so it wins over the ambiguous id.
        var r = ModelContext.Resolve(new ContextEvidence(
            ModelLineName: "Sonnet 4.6 (1M context)", RunningModelId: "claude-sonnet-4-6"));
        Assert.Equal(ModelContext.ExtendedWindow, r.Tokens);
        Assert.Equal(ContextWindowSource.ModelLine, r.Source);
        Assert.Equal("Sonnet 4.6 (1M context)", r.Model);
    }

    [Fact]
    public void Resolve_StaleModelLineFromAnotherFamilyIsIgnored()
    {
        // Claude Code can move a session to another family on its own (an Opus limit falling back to
        // Sonnet) without writing a /model line, leaving the old line describing a model that is no
        // longer answering. The id that *is* answering wins.
        var r = ModelContext.Resolve(new ContextEvidence(
            ModelLineName: "Opus 4.8 (1M context)", ModelIdSinceLine: "claude-sonnet-4-6",
            RunningModelId: "claude-sonnet-4-6"));
        Assert.Equal(ModelContext.DefaultWindow, r.Tokens);
        Assert.Equal(ContextWindowSource.ModelId, r.Source);
    }

    [Fact]
    public void Resolve_ConfiguredOptInBeatsVariantStrippedTranscriptId()
    {
        // The exact shape that made a 1M Opus 5 session read as 200k: the transcript's message.model is
        // stripped to "claude-opus-5", and only settings.json still carries the "[1m]" opt-in.
        var r = ModelContext.Resolve(new ContextEvidence(
            RunningModelId: "claude-opus-5", ConfiguredModelId: "opus[1m]"));
        Assert.Equal(ModelContext.ExtendedWindow, r.Tokens);
        Assert.Equal(ContextWindowSource.ConfiguredOptIn, r.Source);
        Assert.Equal("claude-opus-5", r.Model);   // label the model answering, not the settings alias
    }

    [Fact]
    public void Resolve_ConfiguredOptInForAnotherFamilyIsIgnored()
    {
        // settings.json says opus-with-1M, but a Sonnet 4.6 is answering: the marker says nothing about
        // the model the session actually moved to.
        var r = ModelContext.Resolve(new ContextEvidence(
            RunningModelId: "claude-sonnet-4-6", ConfiguredModelId: "opus[1m]"));
        Assert.Equal(ModelContext.DefaultWindow, r.Tokens);
        Assert.Equal(ContextWindowSource.ModelId, r.Source);
    }

    [Theory]
    [InlineData(255_914, ModelContext.ExtendedWindow)]   // >200k proves the window isn't 200k
    [InlineData(1_400_000, 2_000_000)]                   // a future 2M model, rounded to the next million
    public void Resolve_ObservedPromptRatchetsTheWindowUp(long observed, int expected)
    {
        // The rules go stale with every model release; the tokens in the transcript don't. The API rejects
        // a prompt bigger than the window, so an oversized prompt is proof and overrides every guess.
        var r = ModelContext.Resolve(new ContextEvidence(
            RunningModelId: "claude-sonnet-4-6", MaxObservedPrompt: observed));
        Assert.Equal(expected, r.Tokens);
        Assert.Equal(ContextWindowSource.Observed, r.Source);
        Assert.Equal("claude-sonnet-4-6", r.Model);
    }

    [Fact]
    public void Resolve_ObservedPromptThatFitsChangesNothing()
    {
        var r = ModelContext.Resolve(new ContextEvidence(
            RunningModelId: "claude-sonnet-4-6", MaxObservedPrompt: 199_000));
        Assert.Equal(ModelContext.DefaultWindow, r.Tokens);
        Assert.Equal(ContextWindowSource.ModelId, r.Source);
    }

    [Fact]
    public void Resolve_NoSignalsAtAll_AssumesStandardWindow()
    {
        var r = ModelContext.Resolve(new ContextEvidence());
        Assert.Equal(ModelContext.DefaultWindow, r.Tokens);
        Assert.Equal(ContextWindowSource.Assumed, r.Source);
        Assert.Null(r.Model);
    }
}

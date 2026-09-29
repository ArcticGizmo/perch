using Perch.Data.Control;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// CP22 (docs/review-fixes-plan.md): streaming a long reply must not cost O(n²). A <see cref="TextPart"/>
/// accumulates deltas in a builder and materialises its text only when read; <see cref="StreamingTail"/> decides
/// how the still-forming tail block renders (plain while inside an open code fence, slower cadence when large).
/// </summary>
public class StreamingTextTests
{
    // ── TextPart accumulation ────────────────────────────────────────────────────

    [Fact]
    public void Deltas_AccumulateToTheirConcatenation()
    {
        var conv = new SessionConversation();
        conv.AddUserPrompt("go");
        var rng = new Random(7);
        var expected = new System.Text.StringBuilder();
        for (int i = 0; i < 2000; i++)
        {
            var delta = new string((char)('a' + rng.Next(26)), rng.Next(1, 30)) + (i % 50 == 0 ? "\n\n" : "");
            expected.Append(delta);
            conv.Apply(new TextDeltaEvent(delta));
        }

        var part = Assert.IsType<TextPart>(Assert.Single(Assert.IsType<AssistantMessageItem>(conv.Items[1]).Parts));
        Assert.True(part.IsStreaming);
        Assert.Equal(expected.Length, part.Length);
        Assert.Equal(expected.ToString(), part.Text);
    }

    [Fact]
    public void Text_IsCachedUntilTheNextDelta()
    {
        var conv = new SessionConversation();
        conv.AddUserPrompt("go");
        conv.Apply(new TextDeltaEvent("Hello"));
        var part = (TextPart)((AssistantMessageItem)conv.Items[1]).Parts[0];

        var first = part.Text;
        Assert.Same(first, part.Text);   // a second read doesn't re-materialise

        conv.Apply(new TextDeltaEvent(", world"));
        Assert.Equal("Hello, world", part.Text);
        Assert.NotSame(first, part.Text);
    }

    [Fact]
    public void FinalText_ReplacesTheAccumulation_AndLaterReadsAgree()
    {
        var conv = new SessionConversation();
        conv.AddUserPrompt("go");
        conv.Apply(new TextDeltaEvent("Draft tex"));
        conv.Apply(new AssistantTextEvent("Final text."));

        var part = (TextPart)((AssistantMessageItem)conv.Items[1]).Parts[0];
        Assert.False(part.IsStreaming);
        Assert.Equal("Final text.", part.Text);
        Assert.Equal("Final text.".Length, part.Length);
    }

    [Fact]
    public void EmptyDelta_ChangesNothing()
    {
        var conv = new SessionConversation();
        conv.AddUserPrompt("go");
        conv.Apply(new TextDeltaEvent("abc"));
        var part = (TextPart)((AssistantMessageItem)conv.Items[1]).Parts[0];
        var before = part.Text;
        conv.Apply(new TextDeltaEvent(""));
        Assert.Same(before, part.Text);
    }

    // ── StreamingTail.TryOpenFence ───────────────────────────────────────────────

    [Theory]
    [InlineData("```csharp\nvar x = 1;\n", "var x = 1;")]
    [InlineData("```\nline 1\nline 2", "line 1\nline 2")]
    [InlineData("~~~py\nprint(1)\n", "print(1)")]
    [InlineData("   ```\nindented opener\n", "indented opener")]      // up to three spaces of indent
    [InlineData("\n\n```js\nafter blank lines\n", "after blank lines")]
    [InlineData("```cs\r\nvar a;\r\nvar b;\r\n", "var a;\nvar b;")]   // CRLF: carriage returns dropped
    [InlineData("````\n```\nstill inside\n", "```\nstill inside")]    // a shorter run can't close a longer fence
    [InlineData("```\ncode\n~~~\n", "code\n~~~")]                     // the other fence char doesn't close it
    [InlineData("```\ncode\n``", "code\n``")]                         // a closing fence still arriving
    [InlineData("```\ncode\n```x\n", "code\n```x")]                   // a closer can't carry an info string
    public void OpenFence_ReturnsTheCodeSoFar(string tail, string expectedCode)
    {
        Assert.True(StreamingTail.TryOpenFence(tail, out var code));
        Assert.Equal(expectedCode, code);
    }

    [Theory]
    [InlineData("```")]
    [InlineData("```pyth")]   // the opening line itself is still arriving
    public void OpenFence_WhileTheOpenerArrives_HasNoCodeYet(string tail)
    {
        Assert.True(StreamingTail.TryOpenFence(tail, out var code));
        Assert.Equal("", code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n\n")]
    [InlineData("A plain paragraph.")]
    [InlineData("```\ncode\n```")]              // closed
    [InlineData("```\ncode\n```   \n")]         // closed, trailing spaces after the closer
    [InlineData("~~~\ncode\n~~~~\n")]           // a longer closing run still closes
    [InlineData("```\ncode\n   ```\n")]         // an indented closer (≤3) still closes
    [InlineData("    ```\nindented code\n")]    // four spaces: an indented code block, not a fence
    [InlineData("``inline``")]                  // two backticks: inline code
    [InlineData("```inline``` code")]           // a backtick in the info string: inline code, not a fence
    [InlineData("- ```\n  in a list\n")]        // a list whose item holds a fence isn't a top-level fence
    public void NotAnOpenFence(string tail)
    {
        Assert.False(StreamingTail.TryOpenFence(tail, out var code));
        Assert.Equal("", code);
    }

    // ── StreamingTail.MinInterval ────────────────────────────────────────────────

    [Fact]
    public void MinInterval_ThrottlesOnlyLargeTails()
    {
        Assert.Equal(TimeSpan.Zero, StreamingTail.MinInterval(StreamingTail.RichBudget, openFence: false));
        Assert.Equal(StreamingTail.SlowCadence, StreamingTail.MinInterval(StreamingTail.RichBudget + 1, openFence: false));

        // An open fence renders as plain text, which is far cheaper — it gets a bigger budget.
        Assert.Equal(TimeSpan.Zero, StreamingTail.MinInterval(StreamingTail.RichBudget + 1, openFence: true));
        Assert.Equal(TimeSpan.Zero, StreamingTail.MinInterval(StreamingTail.PlainBudget, openFence: true));
        Assert.Equal(StreamingTail.SlowCadence, StreamingTail.MinInterval(StreamingTail.PlainBudget + 1, openFence: true));
    }
}

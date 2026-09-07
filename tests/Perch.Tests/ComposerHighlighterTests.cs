using Perch.Data.Control;
using Xunit;

namespace Perch.Tests;

/// <summary>The composer's input tokeniser (docs/session-slash-commands-plan.md): leading slash commands and
/// hyperlinks split out from plain text, with gap-free coverage the renderer can iterate.</summary>
public class ComposerHighlighterTests
{
    private static (InputTokenKind Kind, string Text) Span(string src, InputToken t) => (t.Kind, src.Substring(t.Start, t.Length));

    [Fact]
    public void Empty_IsEmpty() => Assert.Empty(ComposerHighlighter.Tokenize(""));

    [Fact]
    public void PlainText_IsOneTextSpan()
    {
        var t = Assert.Single(ComposerHighlighter.Tokenize("hello there"));
        Assert.Equal((InputTokenKind.Text, "hello there"), (t.Kind, "hello there"[..t.Length]));
    }

    [Fact]
    public void LeadingCommand_Only()
    {
        var toks = ComposerHighlighter.Tokenize("/context");
        var t = Assert.Single(toks);
        Assert.Equal(InputTokenKind.Command, t.Kind);
    }

    [Fact]
    public void CommandWithArgs_CommandThenText()
    {
        const string src = "/compact keep the tests";
        var toks = ComposerHighlighter.Tokenize(src);
        Assert.Equal(2, toks.Count);
        Assert.Equal((InputTokenKind.Command, "/compact"), Span(src, toks[0]));
        Assert.Equal((InputTokenKind.Text, " keep the tests"), Span(src, toks[1]));
    }

    [Fact]
    public void LeadingWhitespace_BeforeCommand()
    {
        const string src = "  /model";
        var toks = ComposerHighlighter.Tokenize(src);
        Assert.Equal((InputTokenKind.Text, "  "), Span(src, toks[0]));
        Assert.Equal((InputTokenKind.Command, "/model"), Span(src, toks[1]));
    }

    [Fact]
    public void SlashMidText_IsNotACommand()
    {
        var toks = ComposerHighlighter.Tokenize("run the /context command");
        Assert.All(toks, t => Assert.Equal(InputTokenKind.Text, t.Kind));
    }

    [Fact]
    public void UnknownCommand_IsPlainText()
    {
        // "/notacommand" isn't a built-in → no Command span (stays plain text).
        var toks = ComposerHighlighter.Tokenize("/notacommand");
        Assert.All(toks, t => Assert.Equal(InputTokenKind.Text, t.Kind));
        Assert.DoesNotContain(toks, t => t.Kind == InputTokenKind.Command);
    }

    [Fact]
    public void KnownCommandPredicate_RecognisesExtraNames()
    {
        // The window passes a predicate that also accepts advertised skills (e.g. "grill-me").
        var toks = ComposerHighlighter.Tokenize("/grill-me now", name => name == "grill-me");
        Assert.Equal(InputTokenKind.Command, toks[0].Kind);
        Assert.Equal("/grill-me", "/grill-me now"[..toks[0].Length]);
    }

    [Fact]
    public void Link_InSentence_TrimsTrailingPunctuation()
    {
        const string src = "see https://example.com/x, then stop";
        var toks = ComposerHighlighter.Tokenize(src);
        var link = Assert.Single(toks, t => t.Kind == InputTokenKind.Link);
        Assert.Equal("https://example.com/x", src.Substring(link.Start, link.Length));   // comma trimmed
    }

    [Fact]
    public void Coverage_IsGapFreeAndOrdered()
    {
        const string src = "/init then see http://a.b/c ok";
        var toks = ComposerHighlighter.Tokenize(src);
        int pos = 0;
        foreach (var t in toks) { Assert.Equal(pos, t.Start); pos += t.Length; }
        Assert.Equal(src.Length, pos);
        Assert.Contains(toks, t => t.Kind == InputTokenKind.Command);
        Assert.Contains(toks, t => t.Kind == InputTokenKind.Link);
    }
}

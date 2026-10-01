using Perch.Data;
using Xunit;

namespace Perch.Tests;

public class CodeHighlightTests
{
    private static string Reconstruct(IEnumerable<(string Text, CodeToken Kind)> toks) =>
        string.Concat(toks.Select(t => t.Text));

    // Review fixes CP24: language profiles are built once and shared, so concurrent highlighting (render threads,
    // several windows) must see the same answer as a single thread, including for the profiles derived from
    // another (TypeScript from JavaScript, C++ from C) that must not leak their extra keywords into the base.
    [Fact]
    public void Shared_profiles_give_the_same_tokens_under_concurrency()
    {
        var samples = new (string Lang, string Code)[]
        {
            ("js", "interface Foo { const x = 1; }"),
            ("ts", "interface Foo { const x: string = 'a'; }"),
            ("c", "class A { int x; }"),
            ("cpp", "class A { int x; };"),
            ("sql", "select * from t where id = 1"),
            ("bash", "echo $HOME # hi"),
        };
        var expected = samples.Select(s => CodeHighlight.Tokenize(s.Lang, s.Code).ToList()).ToList();
        Parallel.For(0, 400, i =>
        {
            var s = samples[i % samples.Length];
            Assert.Equal(expected[i % samples.Length], CodeHighlight.Tokenize(s.Lang, s.Code));
        });
        // Derived profiles didn't add to their base: "interface" is a TS keyword, not a JS one; "class" C++ not C.
        Assert.DoesNotContain(("interface", CodeToken.Keyword), expected[0]);
        Assert.Contains(("interface", CodeToken.Keyword), expected[1]);
        Assert.DoesNotContain(("class", CodeToken.Keyword), expected[2]);
        Assert.Contains(("class", CodeToken.Keyword), expected[3]);
    }

    // Every span's text concatenated must equal the input exactly — no char dropped or duplicated.
    [Theory]
    [InlineData("bash", "if [ -f x ]; then echo \"$HOME/#notacomment\"; fi  # done\n")]
    [InlineData("csharp", "public int F(int x) => x + 0xFF; // add\n/* block */\nvar s = \"hi\\n\";")]
    [InlineData("python", "def f(x):\n    return '''triple\nline''' # c\n")]
    [InlineData("json", "{ \"a\": 1, \"b\": true, \"c\": null }")]
    [InlineData("sql", "SELECT * FROM t WHERE x = 1 -- note")]
    [InlineData("", "no language just text 123")]
    [InlineData("totally-unknown-lang", "still text 123 // not a comment")]
    public void Reconstructs_Input_Exactly(string lang, string code)
    {
        var toks = CodeHighlight.Tokenize(lang, code);
        Assert.Equal(code, Reconstruct(toks));
    }

    [Fact]
    public void UnknownLanguage_IsSinglePlainSpan()
    {
        var toks = CodeHighlight.Tokenize("brainfuck", "++++ // nope");
        Assert.Single(toks);
        Assert.Equal(CodeToken.Plain, toks[0].Kind);
    }

    [Fact]
    public void EmptyCode_YieldsNothing()
    {
        Assert.Empty(CodeHighlight.Tokenize("bash", ""));
    }

    private static bool Has(IEnumerable<(string Text, CodeToken Kind)> toks, string text, CodeToken kind) =>
        toks.Any(t => t.Text == text && t.Kind == kind);

    [Fact]
    public void Bash_HighlightsCommentsKeywordsStringsAndVars()
    {
        var toks = CodeHighlight.Tokenize("bash", "if true; then\n  echo \"hi\"  # greet\nfi\nX=$HOME\n");
        Assert.Contains(toks, t => t.Kind == CodeToken.Comment && t.Text.Contains("# greet"));
        Assert.True(Has(toks, "if", CodeToken.Keyword));
        Assert.True(Has(toks, "fi", CodeToken.Keyword));
        Assert.True(Has(toks, "echo", CodeToken.Type));       // builtin/command
        Assert.True(Has(toks, "\"hi\"", CodeToken.Str));
        Assert.True(Has(toks, "$HOME", CodeToken.Type));      // variable
    }

    [Fact]
    public void Bash_HashInsideDoubleQuotesIsNotAComment()
    {
        var toks = CodeHighlight.Tokenize("bash", "echo \"a#b\"\n");
        Assert.True(Has(toks, "\"a#b\"", CodeToken.Str));
        Assert.DoesNotContain(toks, t => t.Kind == CodeToken.Comment);
    }

    [Fact]
    public void CSharp_KeywordsTypesNumbersCommentsAndCalls()
    {
        var toks = CodeHighlight.Tokenize("cs", "public int Add(int a) { return a + 0x10; } // sum");
        Assert.True(Has(toks, "public", CodeToken.Keyword));
        Assert.True(Has(toks, "int", CodeToken.Type));
        Assert.True(Has(toks, "Add", CodeToken.Function));    // identifier before '('
        Assert.True(Has(toks, "0x10", CodeToken.Number));
        Assert.Contains(toks, t => t.Kind == CodeToken.Comment && t.Text.Contains("// sum"));
    }

    [Fact]
    public void Python_TripleQuotedStringSpansLines()
    {
        var toks = CodeHighlight.Tokenize("python", "x = '''a\nb\nc''' # done");
        Assert.Contains(toks, t => t.Kind == CodeToken.Str && t.Text.Contains("\n") && t.Text.Contains("a"));
        Assert.True(Has(toks, "x", CodeToken.Plain) || toks.Any(t => t.Text.StartsWith("x")));
    }

    [Fact]
    public void Sql_IsCaseInsensitive()
    {
        var upper = CodeHighlight.Tokenize("sql", "SELECT 1");
        var lower = CodeHighlight.Tokenize("sql", "select 1");
        Assert.True(Has(upper, "SELECT", CodeToken.Keyword));
        Assert.True(Has(lower, "select", CodeToken.Keyword));
    }

    [Fact]
    public void UnterminatedBlockComment_RunsToEnd()
    {
        var toks = CodeHighlight.Tokenize("cs", "ok /* never closed");
        Assert.Contains(toks, t => t.Kind == CodeToken.Comment && t.Text == "/* never closed");
    }
}

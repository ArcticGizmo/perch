using Perch.Data;
using Xunit;

namespace Perch.Tests;

// A drag across the rendered Markdown (session thread, doc preview) copies the Markdown that produced the selection.
// The headline guarantee: selecting a whole message copies exactly what the message's copy button does (its raw
// Markdown), and a partial selection keeps the syntax around the parts it covers.
public class MarkdownCopyTests
{
    // A raw string literal takes the line endings of this file as checked out — CRLF on a Windows runner with
    // core.autocrlf — so normalise to LF; the expectations below spell their newlines as \n. (CRLF sources are
    // covered on purpose by Windows_line_endings_round_trip.)
    private static readonly string Sample = """
        # Selection test: heading one

        This first paragraph sits directly under an h1. It contains **bold text**, *italic text*, ~~strikethrough~~, `inline code`, and a link to [Anthropic](https://www.anthropic.com).

        Second paragraph, short.

        ## Heading two with a rule

        ### Lists

        - First bullet item
        - Second bullet item with `inline code` in it
          - Nested bullet one
          - Nested bullet two
            - Doubly nested bullet
        - Third bullet item

        1. Ordered item one
        2. Ordered item two
        3. Ordered item three

        - [ ] An unchecked task
        - [x] A checked task

        ### Code

        ```csharp
        public static int Add(int a, int b)
        {
            // A comment, then a long line: 0123456789012345678901234567890123456789012345678901234567890123456789
            return a + b;
        }
        ```

        ### Table

        | Name | Kind | Count |
        |------|------|------:|
        | Alpha | heading | 1 |
        | Beta | paragraph | 22 |
        | Gamma | table cell | 333 |

        > A block quote that spans enough words to wrap onto a second line, so the quote bar and the selection wash can be checked together.

        ---

        Final paragraph after a thematic break. End of test.
        """.ReplaceLineEndings("\n");

    // Select every rendered block, start to end — what a drag from the top of a message to its bottom does.
    private static string SelectAll(string md, bool markdown = true) =>
        MarkdownCopy.Compose(MarkdownCopy.Blocks(md).Select(b => new MarkdownCopy.Piece(b.Map, b.Text, 0, b.Text.Length)).ToList(), markdown);

    // Select from the first rendered occurrence of `from` to the end of the next occurrence of `to` (at or after
    // the start block), as a drag over those words would.
    private static string Select(string md, string from, string to, bool markdown = true)
    {
        var blocks = MarkdownCopy.Blocks(md);
        int sb = blocks.ToList().FindIndex(b => b.Text.Contains(from, StringComparison.Ordinal));
        Assert.True(sb >= 0, $"no block renders '{from}'");
        int sc = blocks[sb].Text.IndexOf(from, StringComparison.Ordinal);
        int eb = -1, ec = -1;
        for (int i = sb; i < blocks.Count && eb < 0; i++)
        {
            int at = blocks[i].Text.IndexOf(to, i == sb ? sc : 0, StringComparison.Ordinal);
            if (at >= 0) { eb = i; ec = at + to.Length; }
        }
        Assert.True(eb >= 0, $"no block after '{from}' renders '{to}'");
        var pieces = new List<MarkdownCopy.Piece>();
        for (int i = sb; i <= eb; i++)
            pieces.Add(new MarkdownCopy.Piece(blocks[i].Map, blocks[i].Text,
                i == sb ? sc : 0, i == eb ? ec : blocks[i].Text.Length));
        return MarkdownCopy.Compose(pieces, markdown);
    }

    [Fact]
    public void Selecting_a_whole_message_copies_what_the_copy_button_does()
    {
        Assert.Equal(Sample, SelectAll(Sample));
    }

    [Theory]
    [InlineData("Just one paragraph.")]
    [InlineData("**Bold start** and *italic end*")]
    [InlineData("[link first](https://example.com) then text")]
    [InlineData("Para one.\n\nPara two with `code`.")]
    [InlineData("1. one\n2. two\n   - nested\n3. three")]
    [InlineData("> quoted **bold**\n> second line\n\nafter")]
    [InlineData("Line with hard break  \nnext line")]
    [InlineData("```\nno language\n```")]
    [InlineData("    indented code\n    block")]
    [InlineData("| a | b |\n|---|---|\n| 1 | 2 |")]
    [InlineData("---\ntitle: x\n---\n\n# After frontmatter")]
    [InlineData("<div>raw html</div>\n\ntext")]
    [InlineData("Autolink <https://example.com> and www.example.com")]
    [InlineData("Image ![alt](pic.png) inline")]
    [InlineData("Setext heading\n==============\n\nbody")]
    public void Selecting_all_of_any_document_round_trips_its_markdown(string md)
    {
        Assert.Equal(md, SelectAll(md));
    }

    [Fact]
    public void Windows_line_endings_round_trip()
    {
        string crlf = Sample.ReplaceLineEndings("\r\n");
        Assert.Equal(crlf, SelectAll(crlf));
    }

    [Fact]
    public void Surrounding_blank_lines_are_not_part_of_the_selection()
    {
        Assert.Equal("# Title\n\nBody.", SelectAll("\n\n# Title\n\nBody.\n\n"));
    }

    [Fact]
    public void Every_block_map_measures_its_rendered_text()
    {
        foreach (var (map, text) in MarkdownCopy.Blocks(Sample))
            Assert.Equal(text.Length, map.Length);
    }

    [Theory]
    [InlineData("Selection test", "heading one", "# Selection test: heading one")]           // a whole heading takes its hashes
    [InlineData("heading one", "This first", "heading one\n\nThis first")]                   // heading into body, mid-line
    [InlineData("bold text", "bold text", "**bold text**")]                                  // emphasis keeps its delimiters
    [InlineData("italic text", "italic text", "*italic text*")]
    [InlineData("strikethrough", "strikethrough", "~~strikethrough~~")]
    [InlineData("inline code, and", "inline code", "`inline code`")]
    [InlineData("Anthropic", "Anthropic", "[Anthropic](https://www.anthropic.com)")]          // a link keeps its target
    [InlineData("old te", "old te", "old te")]                                               // inside a span: just the characters
    [InlineData("First bullet item", "Doubly nested bullet",
        "- First bullet item\n- Second bullet item with `inline code` in it\n  - Nested bullet one\n  - Nested bullet two\n    - Doubly nested bullet")]
    [InlineData("Ordered item one", "Ordered item three", "1. Ordered item one\n2. Ordered item two\n3. Ordered item three")]
    [InlineData("￼ An unchecked task", "A checked task", "- [ ] An unchecked task\n- [x] A checked task")]
    [InlineData("A block quote", "checked together.",
        "> A block quote that spans enough words to wrap onto a second line, so the quote bar and the selection wash can be checked together.")]
    [InlineData("Alpha", "1", "| Alpha | heading | 1 |")]                                    // a table row keeps its pipes
    [InlineData("Beta", "22", "| Beta | paragraph | 22 |")]
    [InlineData("Gamma", "table cell", "| Gamma | table cell")]                               // a row cut mid-way
    [InlineData("Lists", "First bullet", "### Lists\n\n- First bullet")]
    public void Partial_selections_copy_the_markdown_they_cover(string from, string to, string expected)
    {
        Assert.Equal(expected, Select(Sample, from, to));
    }

    [Fact]
    public void A_whole_code_block_keeps_its_fences()
    {
        string expected = """
            ```csharp
            public static int Add(int a, int b)
            {
                // A comment, then a long line: 0123456789012345678901234567890123456789012345678901234567890123456789
                return a + b;
            }
            ```
            """.ReplaceLineEndings("\n");
        Assert.Equal(expected, Select(Sample, "public static", "return a + b;\n}"));
    }

    [Fact]
    public void Part_of_a_code_block_copies_just_those_characters()
    {
        Assert.Equal("int Add(int a, int b)\n{", Select(Sample, "int Add", "b)\n{"));
    }

    [Fact]
    public void Plain_text_mode_copies_the_rendered_characters()
    {
        Assert.Equal("bold text", Select(Sample, "bold text", "bold text", markdown: false));
        Assert.Equal("Anthropic", Select(Sample, "Anthropic", "Anthropic", markdown: false));
    }

    [Fact]
    public void Separate_documents_join_with_a_blank_line()
    {
        string a = "First **message**.", b = "- second\n- message";
        var pieces = MarkdownCopy.Blocks(a).Concat(MarkdownCopy.Blocks(b))
            .Select(x => new MarkdownCopy.Piece(x.Map, x.Text, 0, x.Text.Length)).ToList();
        Assert.Equal(a + "\n\n" + b, MarkdownCopy.Compose(pieces));
    }

    [Fact]
    public void Identical_text_in_two_messages_is_still_two_documents()
    {
        string a = "Same.", b = "Same.";
        var pieces = MarkdownCopy.Blocks(a).Concat(MarkdownCopy.Blocks(b))
            .Select(x => new MarkdownCopy.Piece(x.Map, x.Text, 0, x.Text.Length)).ToList();
        Assert.Equal("Same.\n\nSame.", MarkdownCopy.Compose(pieces));
    }

    [Fact]
    public void Non_markdown_text_copies_as_rendered()
    {
        var md = MarkdownCopy.Blocks("Some *prose*.")[0];
        var pieces = new List<MarkdownCopy.Piece>
        {
            new(null, "tool output\nline two", 5, 20),
            new(md.Map, md.Text, 0, md.Text.Length),
        };
        Assert.Equal("output\nline two\n\nSome *prose*.", MarkdownCopy.Compose(pieces));
    }
}

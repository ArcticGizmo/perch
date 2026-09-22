using System.Linq;
using Perch.Statusline;
using Xunit;

namespace Perch.Tests;

/// <summary>The mustache-ish statusline engine: interpolation, the filter pipeline, conditionals and
/// sections, and colour segmentation. Renders against the shared <see cref="StatuslineSample"/> payload
/// plus a few hand-built payloads for the null/absent edge cases.</summary>
public sealed class StatuslineTemplateTests
{
    private static string Render(string tpl, TemplateData? data = null) =>
        StatuslineTemplate.RenderToString(tpl, data ?? StatuslineSample.Data(), color: false);

    // ── interpolation ────────────────────────────────────────────────────────────────
    [Fact]
    public void Interpolates_a_dotted_path() =>
        Assert.Equal("Opus", Render("{{model.display_name}}"));

    [Fact]
    public void Missing_path_renders_empty() =>
        Assert.Equal("", Render("{{not.a.field}}"));

    [Fact]
    public void Literal_text_passes_through() =>
        Assert.Equal("model=Opus", Render("model={{model.display_name}}"));

    // ── filters ──────────────────────────────────────────────────────────────────────
    [Fact]
    public void Money_formats_two_decimals() =>
        Assert.Equal("0.42", Render("{{cost.total_cost_usd|money}}"));

    [Fact]
    public void Pct_rounds_and_suffixes() =>
        Assert.Equal("34%", Render("{{context_window.used_percentage|pct}}"));

    [Fact]
    public void Round_rounds_to_whole() =>
        Assert.Equal("41", Render("{{rate_limits.seven_day.used_percentage|round}}"));

    [Fact]
    public void Bar_draws_a_proportional_block_bar() =>
        // 34% of 10 cells -> 3 filled
        Assert.Equal("███░░░░░░░", Render("{{context_window.used_percentage|bar:10}}"));

    [Fact]
    public void Upper_uppercases() =>
        Assert.Equal("HIGH", Render("{{effort.level|upper}}"));

    [Fact]
    public void K_formats_thousands() =>
        Assert.Equal("68k", Render("{{context_window.total_input_tokens|k}}"));

    [Fact]
    public void Default_fills_in_when_empty() =>
        Assert.Equal("n/a", Render("{{not.a.field|default:n/a}}"));

    [Fact]
    public void Trunc_ellipsises_to_length()
    {
        var outp = Render("{{workspace.current_dir|trunc:10}}");
        Assert.Equal(10, outp.Length);
        Assert.EndsWith("…", outp);
    }

    [Fact]
    public void Filters_chain_left_to_right() =>
        // upper("Opus") -> "OPUS", then trunc:2 -> "O…"
        Assert.Equal("O…", Render("{{model.display_name|upper|trunc:2}}"));

    // ── conditionals ─────────────────────────────────────────────────────────────────
    [Fact]
    public void If_renders_body_when_truthy() =>
        Assert.Equal("PR", Render("{{#if pr.number}}PR{{/if}}"));

    [Fact]
    public void If_hides_body_when_absent()
    {
        var data = TemplateData.Parse("""{ "model": { "display_name": "Opus" } }""");
        Assert.Equal("", Render("{{#if pr.number}}PR{{/if}}", data));
    }

    [Fact]
    public void Unless_is_the_inverse()
    {
        var data = TemplateData.Parse("""{ "x": 1 }""");
        Assert.Equal("no-pr", Render("{{#unless pr.number}}no-pr{{/unless}}", data));
        Assert.Equal("", Render("{{#unless pr.number}}no-pr{{/unless}}"));   // sample HAS a pr
    }

    [Fact]
    public void If_numeric_comparison()
    {
        Assert.Equal("", Render("{{#if context_window.used_percentage > 80}}HI{{/if}}"));   // sample is 34
        var hot = TemplateData.Parse("""{ "context_window": { "used_percentage": 87 } }""");
        Assert.Equal("HI", Render("{{#if context_window.used_percentage > 80}}HI{{/if}}", hot));
    }

    [Fact]
    public void If_string_equality()
    {
        Assert.Equal("P", Render("{{#if pr.review_state == 'pending'}}P{{/if}}"));
        Assert.Equal("", Render("{{#if pr.review_state == 'approved'}}A{{/if}}"));
    }

    [Fact]
    public void Inverted_section_renders_when_falsy()
    {
        var clean = TemplateData.Parse("""{ "git": { "dirty": false } }""");
        Assert.Equal("clean", Render("{{^git.dirty}}clean{{/git.dirty}}", clean));
        Assert.Equal("", Render("{{^git.dirty}}clean{{/git.dirty}}"));   // sample is dirty:true
    }

    [Fact]
    public void Truthy_section_on_object() =>
        Assert.Equal("has-pr", Render("{{#pr}}has-pr{{/pr}}"));

    [Fact]
    public void Present_null_is_falsy_and_empty()
    {
        var data = TemplateData.Parse("""{ "context_window": { "used_percentage": null } }""");
        Assert.Equal("", Render("{{#if context_window.used_percentage}}X{{/if}}", data));
        Assert.Equal("z", Render("{{context_window.used_percentage|default:z}}", data));
    }

    [Fact]
    public void Multiline_template_keeps_newline()
    {
        var outp = Render("a{{model.display_name}}\nb");
        Assert.Equal("aOpus\nb", outp);
    }

    // ── soft breaks ("\" + newline: editor readability wraps that must NOT split the line) ─────────
    [Fact]
    public void Soft_break_joins_the_line()
    {
        Assert.Equal("abcdef", Render("abc\\\ndef"));
        Assert.Equal("aOpus b", Render("a{{model.display_name}}\\\n b"));
        Assert.Equal("aOpus b", Render("a{{model.display_name}} \\\r\nb"));   // CRLF form
    }

    [Fact]
    public void Soft_break_does_not_swallow_a_hard_newline()
    {
        // A bare newline (no preceding backslash) still splits; only "\<newline>" is joined.
        Assert.Equal("a\nb", Render("a\nb"));
        Assert.Equal("a\\b", Render("a\\b"));   // a lone backslash (not before a newline) is literal
    }

    // ── RenderPlaced: rendered segments carry their source span (for the caret→preview highlight) ──
    [Fact]
    public void RenderPlaced_reports_tag_source_spans_in_original_coordinates()
    {
        const string tpl = "hi {{model.display_name}}!";
        var placed = StatuslineTemplate.RenderPlaced(tpl, StatuslineSample.Data());

        var tag = placed.Single(p => p.IsTag);
        Assert.Equal("Opus", tag.Segment.Text);
        int start = tpl.IndexOf("{{model", StringComparison.Ordinal);
        Assert.Equal(start, tag.SourceStart);
        Assert.Equal("{{model.display_name}}".Length, tag.SourceLength);
        // the span brackets the whole {{…}} token, so a caret anywhere inside maps to this element
        Assert.True(start <= start + 5 && start + 5 < tag.SourceStart + tag.SourceLength);
    }

    [Fact]
    public void RenderPlaced_only_the_caret_token_covers_a_given_offset()
    {
        // Regression: at caret 0 only the leading {{model.display_name}} should match — not a later
        // {{git.branch}}. Every tag's span must bracket exactly its own token.
        var tpl = StatuslineDefaults.All.First(p => p.Name == StatuslineDefaults.PerchDefaultName).Template!;
        var placed = StatuslineTemplate.RenderPlaced(tpl, StatuslineSample.Data());
        var tags = placed.Where(p => p.IsTag).ToList();

        int Covering(int caret) => tags.Count(t => caret >= t.SourceStart && caret < t.SourceStart + t.SourceLength);
        Assert.Equal(1, Covering(0));    // only model.display_name spans offset 0

        // no two tag spans overlap (sorted by start, each begins at/after the previous one's end)
        var sorted = tags.OrderBy(t => t.SourceStart).ToList();
        for (int i = 1; i < sorted.Count; i++)
            Assert.True(sorted[i].SourceStart >= sorted[i - 1].SourceStart + sorted[i - 1].SourceLength);
    }

    [Fact]
    public void RenderPlaced_keeps_spans_aligned_past_a_soft_break()
    {
        // The var sits AFTER a soft break; its reported span must be in the ORIGINAL string's coordinates
        // (including the "\<newline>"), so the editor caret over it hit-tests correctly.
        const string tpl = "a\\\n{{model.display_name}}";
        var placed = StatuslineTemplate.RenderPlaced(tpl, StatuslineSample.Data());
        var tag = placed.Single(p => p.IsTag);
        Assert.Equal("Opus", tag.Segment.Text);
        Assert.Equal(tpl.IndexOf("{{model", StringComparison.Ordinal), tag.SourceStart);
    }

    // ── smart separators ({{sep}}) ─────────────────────────────────────────────────────
    [Fact]
    public void Separator_joins_present_cells()
    {
        var d = TemplateData.Parse("""{"a":"AAA","b":"BBB"}""");
        Assert.Equal("AAA | BBB", Render("{{a}}{{sep}}{{b}}", d));
    }

    [Fact]
    public void Separator_collapses_when_a_middle_cell_is_empty()
    {
        // The classic "aaaa |  | bbbb" problem: an empty conditional between two seps → one sep, not two.
        const string tpl = "{{a}}{{sep}}{{#if mid}}{{mid}}{{/if}}{{sep}}{{b}}";
        Assert.Equal("AAA | BBB", Render(tpl, TemplateData.Parse("""{"a":"AAA","b":"BBB"}""")));
        Assert.Equal("AAA | MID | BBB", Render(tpl, TemplateData.Parse("""{"a":"AAA","b":"BBB","mid":"MID"}""")));
    }

    [Fact]
    public void Separator_drops_at_the_ends()
    {
        Assert.Equal("BBB", Render("{{a}}{{sep}}{{b}}", TemplateData.Parse("""{"b":"BBB"}""")));   // leading empty
        Assert.Equal("AAA", Render("{{a}}{{sep}}{{b}}", TemplateData.Parse("""{"a":"AAA"}""")));   // trailing empty
    }

    [Fact]
    public void Separator_custom_glyph()
    {
        var d = TemplateData.Parse("""{"a":"A","b":"B"}""");
        Assert.Equal("A · B", Render("{{a}}{{sep:·}}{{b}}", d));
    }

    // ── custom hex colours ─────────────────────────────────────────────────────────────
    [Fact]
    public void Color_hex_sets_a_custom_rgb()
    {
        var segs = StatuslineTemplate.Render("{{model.display_name|color:#ff8800}}", StatuslineSample.Data());
        var seg = Assert.Single(segs);
        Assert.Equal("Opus", seg.Text);
        Assert.Equal(0xff8800, seg.Rgb);
    }

    [Fact]
    public void Color_hex_three_digit_expands()
    {
        var segs = StatuslineTemplate.Render("{{model.display_name|color:#f80}}", StatuslineSample.Data());
        Assert.Equal(0xff8800, segs[0].Rgb);
    }

    [Fact]
    public void Color_hex_emits_truecolor_ansi()
    {
        var ansi = StatuslineTemplate.RenderToString("{{model.display_name|color:1a2b3c}}", StatuslineSample.Data());
        Assert.Contains("\x1b[38;2;26;43;60m", ansi);   // 0x1a,0x2b,0x3c
    }

    [Fact]
    public void ParseHex_accepts_forms_and_rejects_junk()
    {
        Assert.Equal(0x46c6b8, StatusColors.ParseHex("#46c6b8"));
        Assert.Equal(0x46c6b8, StatusColors.ParseHex("46c6b8"));
        Assert.Equal(0xffffff, StatusColors.ParseHex("#fff"));
        Assert.Equal(-1, StatusColors.ParseHex("teal"));
        Assert.Equal(-1, StatusColors.ParseHex("#zzzzzz"));
        Assert.Equal(-1, StatusColors.ParseHex(""));
    }

    // ── colour ───────────────────────────────────────────────────────────────────────
    [Fact]
    public void Color_filter_sets_a_segment_colour()
    {
        var segs = StatuslineTemplate.Render("{{model.display_name|color:teal}}", StatuslineSample.Data());
        var seg = Assert.Single(segs);
        Assert.Equal("Opus", seg.Text);
        Assert.Equal(StatusColor.Teal, seg.Color);
    }

    [Fact]
    public void Ansi_output_wraps_coloured_runs_in_truecolor_escapes()
    {
        var ansi = StatuslineTemplate.RenderToString("{{model.display_name|color:teal}}", StatuslineSample.Data());
        Assert.Contains("\x1b[38;2;70;198;184m", ansi);   // 0x46,0xc6,0xb8
        Assert.Contains("\x1b[0m", ansi);
    }

    [Fact]
    public void Default_template_renders_without_throwing()
    {
        var tpl = StatuslineDefaults.All.First(p => p.Name == StatuslineDefaults.PerchDefaultName).Template!;
        var outp = Render(tpl);
        Assert.Contains("Opus", outp);
        Assert.Contains("PR#30", outp);
    }

    // ── formatters + pace, added for the rate-aware example ──────────────────────────────
    [Fact]
    public void Human_formats_counts()
    {
        Assert.Equal("68k", Render("{{context_window.total_input_tokens|human}}"));   // 68000
        var d = TemplateData.Parse("""{"a":999,"b":1500,"c":2500000}""");
        Assert.Equal("999", Render("{{a|human}}", d));
        Assert.Equal("1k", Render("{{b|human}}", d));
        Assert.Equal("2M", Render("{{c|human}}", d));
    }

    [Fact]
    public void Dur_humanises_milliseconds()
    {
        var d = TemplateData.Parse("""{"a":850,"b":45000,"c":300000,"d":4500000}""");
        Assert.Equal("850ms", Render("{{a|dur}}", d));
        Assert.Equal("45s", Render("{{b|dur}}", d));
        Assert.Equal("5m", Render("{{c|dur}}", d));
        Assert.Equal("1h 15m", Render("{{d|dur}}", d));
    }

    [Fact]
    public void Until_counts_down_to_a_reset()
    {
        long resets = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 90 * 60 + 30;   // ~1h 30m out
        var d = TemplateData.Parse($$"""{"r":{{resets}}}""");
        Assert.Equal("1h 30m", Render("{{r|until}}", d));

        long pastEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 500;
        var past = TemplateData.Parse($$"""{"r":{{pastEpoch}}}""");
        Assert.Equal("0m", Render("{{r|until}}", past));
    }

    [Fact]
    public void PaceColor_matches_the_usage_bar_rule()
    {
        Assert.Equal(StatusColor.Green,  StatuslineTemplate.PaceColor(30, 50));  // >10 behind pace
        Assert.Equal(StatusColor.Yellow, StatuslineTemplate.PaceColor(50, 50));  // on pace
        Assert.Equal(StatusColor.Red,    StatuslineTemplate.PaceColor(70, 50));  // over pace
        Assert.Equal(StatusColor.Green,  StatuslineTemplate.PaceColor(90, 10));  // safe zone (expected < 15)
    }

    [Fact]
    public void Pace_filter_colours_a_rate_window_by_expected_burn()
    {
        long resets = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 9000;   // 2.5h of a 5h window left → expected ~50%

        StatusColor ColorOf(int used)
        {
            var d = TemplateData.Parse($$"""{"u":{{used}},"r":{{resets}}}""");
            return StatuslineTemplate.Render("{{u|pace:r:18000}}", d)[0].Color;
        }

        Assert.Equal(StatusColor.Green,  ColorOf(30));   // behind pace
        Assert.Equal(StatusColor.Yellow, ColorOf(45));   // ~on pace (d ≈ -5)
        Assert.Equal(StatusColor.Red,    ColorOf(70));   // over pace
    }

    [Fact]
    public void Rate_aware_example_renders()
    {
        var tpl = StatuslineDefaults.All.First(p => p.Name == "Rate-aware verbose").Template!;
        var outp = Render(tpl);   // Data() injects resets_at, so the rate windows appear
        Assert.Contains("Context", outp);
        Assert.Contains("5h", outp);
        Assert.Contains("7d", outp);
        Assert.Contains("Opus", outp);
    }

    [Fact]
    public void My_status_line_example_renders()
    {
        var tpl = StatuslineDefaults.All.First(p => p.Name == "My Status Line").Template!;
        var outp = Render(tpl);
        Assert.Contains("Context", outp);
        Assert.Contains("5h", outp);
        Assert.Contains("7d", outp);
        Assert.Contains("Opus", outp);
        // Its distinguishing touch vs "Rate-aware verbose": a "·" between model and effort level.
        Assert.Contains("· high", outp);
    }

    [Fact]
    public void My_status_line_keeps_the_model_row_as_one_unbroken_line()
    {
        // The model/tokens/duration row is a single segment (no soft breaks inside it), matching the authored
        // profile — the earlier version split it across soft breaks and drifted from the original.
        var tpl = StatuslineDefaults.All.First(p => p.Name == "My Status Line").Template!;
        Assert.Contains(
            "{{/if}}]  🔽 {{context_window.total_input_tokens|human}}  🔼 {{context_window.total_output_tokens|human}}  ⏱ {{cost.total_duration_ms|dur}}",
            tpl);
        Assert.DoesNotContain("]  \\\n", tpl);   // no soft break right after the model bracket
    }

    [Fact]
    public void Every_builtin_example_renders_without_throwing()
    {
        foreach (var p in StatuslineDefaults.All)
        {
            var outp = Render(p.Template!);
            Assert.False(string.IsNullOrEmpty(outp), $"'{p.Name}' rendered empty");
        }
    }
}

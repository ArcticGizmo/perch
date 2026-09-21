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
}

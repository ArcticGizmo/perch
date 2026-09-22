using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Perch.Statusline;
using Xunit;

namespace Perch.Tests;

/// <summary>The standalone-script generator: it bakes the template in, produces a <c>node "…"</c>
/// command, and — crucially — renders byte-for-byte the same as the in-process C# engine. The parity
/// cases run the generated script under <c>node</c> and diff its stdout against
/// <see cref="StatuslineTemplate"/>; they self-skip when Node isn't on PATH.</summary>
public sealed class StatuslineScriptTests
{
    [Fact]
    public void Generate_bakes_in_template_and_name()
    {
        var script = StatuslineScript.Generate(new StatuslineProfile
        {
            Name = "My Cool Line",
            Template = "{{model.display_name}}",
        });
        Assert.Contains("\"{{model.display_name}}\"", script);   // JSON-encoded template literal
        Assert.Contains("My Cool Line", script);                  // name echoed into the header comment
        Assert.DoesNotContain("@@TEMPLATE@@", script);
        Assert.DoesNotContain("@@NAME@@", script);
    }

    [Fact]
    public void CommandFor_wraps_the_path_for_node() =>
        Assert.Equal("node \"C:\\a b\\perch-statusline.mjs\"",
            StatuslineScript.CommandFor("C:\\a b\\perch-statusline.mjs"));

    [Fact]
    public void Generate_gates_git_counts_on_template_use()
    {
        var withCounts = StatuslineScript.Generate(new StatuslineProfile
        {
            Name = "g", Template = "{{git.branch}} (+{{git.staged}},-{{git.unstaged}})",
        });
        Assert.Contains("const NEED_GIT_COUNTS = true;", withCounts);
        Assert.Contains("execFileSync", withCounts);   // the git subprocess is present

        var withoutCounts = StatuslineScript.Generate(new StatuslineProfile
        {
            Name = "b", Template = "{{model.display_name}} {{git.branch}}",   // branch only, no counts
        });
        Assert.Contains("const NEED_GIT_COUNTS = false;", withoutCounts);
    }

    [Fact]
    public void Generate_requires_a_template() =>
        Assert.Throws<InvalidOperationException>(() =>
            StatuslineScript.Generate(new StatuslineProfile { Name = "x", Template = null }));

    // ── parity: generated Node script == C# engine ──────────────────────────────────────
    public static TheoryData<string, string> ParityCases() => new()
    {
        // filters + colour
        { "{{model.display_name}} {{context_window.used_percentage|bar:8|color:teal}} {{context_window.used_percentage|pct}} ${{cost.total_cost_usd|money}}",
          """{"model":{"display_name":"Opus"},"context_window":{"used_percentage":34},"cost":{"total_cost_usd":0.4213}}""" },
        // half-up rounding parity (2.5 -> 3, 0.5 -> 1, 23.5 -> 24)
        { "{{a|round}} {{b|round}} {{c|pct}}", """{"a":2.5,"b":0.5,"c":23.5}""" },
        // conditionals: comparison, inverted, string equality
        { "{{#if x > 80}}HI{{/if}}[{{^y}}NOy{{/y}}]{{#if s == 'pending'}}P{{/if}}",
          """{"x":87,"y":false,"s":"pending"}""" },
        // missing / default / trunc / upper / k
        { "{{nope|default:n a}}|{{path|trunc:6}}|{{lvl|upper}}|{{tok|k}}",
          """{"path":"/home/x/project","lvl":"high","tok":68000}""" },
        // the built-in default template, with git supplied in the payload (no cwd -> no injection)
        { StatuslineDefaults.All[0].Template!,
          """{"model":{"display_name":"Opus"},"git":{"branch":"main"},"context_window":{"used_percentage":34},"cost":{"total_cost_usd":0.4213},"pr":{"number":30,"review_state":"pending"}}""" },
        // two-line template
        { StatuslineDefaults.All[2].Template!,
          """{"model":{"display_name":"Opus"},"effort":{"level":"high"},"git":{"branch":"main"},"context_window":{"used_percentage":34},"prompt_cache":{"warm":true,"ttl":"1h"},"rate_limits":{"five_hour":{"used_percentage":23.5}}}""" },
        // formatters added for the rate-aware example
        { "{{a|human}} {{b|human}} {{c|human}}", """{"a":999,"b":68000,"c":2500000}""" },
        { "{{a|dur}} {{b|dur}} {{c|dur}}", """{"a":45000,"b":300000,"c":4500000}""" },
        // pace with a reset far in the future → expected clamps to 0 (< 15 → green), independent of each
        // process's wall clock, so the C# engine and the Node script still agree byte-for-byte
        { "{{u|bar:10|pace:r:18000}} {{u|round|pace:r:18000}}%", """{"u":40,"r":4102444800}""" },
        // soft breaks ("\" + newline) must be dropped identically by both engines
        { "ctx {{context_window.used_percentage|pct}}\\\n  ${{cost.total_cost_usd|money}}",
          """{"context_window":{"used_percentage":34},"cost":{"total_cost_usd":0.4213}}""" },
        // {{sep}} collapse: the empty middle conditional must drop one divider, both engines alike
        { "{{a}}{{sep}}{{#if m}}{{m}}{{/if}}{{sep}}{{b}}", """{"a":"AAA","b":"BBB"}""" },
        { "{{a}}{{sep:·}}{{#if m}}{{m}}{{/if}}{{sep:·}}{{b}}", """{"a":"AAA","m":"MID","b":"BBB"}""" },
        // custom hex colours (named + hex, 3- and 6-digit) render the same truecolor escapes
        { "{{a|color:#ff8800}} {{b|color:1a2b3c}} {{c|color:#f80}} {{d|color:teal}}",
          """{"a":"A","b":"B","c":"C","d":"D"}""" },
    };

    [Theory]
    [MemberData(nameof(ParityCases))]
    public void Generated_script_matches_the_csharp_engine(string template, string payloadJson)
    {
        var node = FindNode();
        if (node is null) return;   // Node not installed on this host — skip (Claude Code guarantees it in the field)

        var expected = StatuslineTemplate.RenderToString(template, TemplateData.Parse(payloadJson), color: true);

        var dir = Path.Combine(Path.GetTempPath(), "perch-sl-node-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var scriptPath = Path.Combine(dir, "line.mjs");
            File.WriteAllText(scriptPath, StatuslineScript.Generate(new StatuslineProfile { Name = "t", Template = template }));
            var actual = RunNode(node, scriptPath, payloadJson);
            Assert.Equal(expected, actual);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private static string? FindNode()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("node", "--version")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            });
            if (p is null) return null;
            p.WaitForExit(5000);
            return p.ExitCode == 0 ? "node" : null;
        }
        catch
        {
            return null;
        }
    }

    private static string RunNode(string node, string scriptPath, string stdin)
    {
        using var p = new Process
        {
            StartInfo = new ProcessStartInfo(node, $"\"{scriptPath}\"")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = new UTF8Encoding(false),
            },
        };
        p.Start();
        p.StandardInput.Write(stdin);
        p.StandardInput.Close();
        var outp = p.StandardOutput.ReadToEnd();
        p.WaitForExit(10000);
        return outp;
    }
}

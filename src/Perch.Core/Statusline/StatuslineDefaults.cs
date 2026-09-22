namespace Perch.Statusline;

using System.Collections.Generic;

/// <summary>The Perch mustache templates a fresh install starts with. All paths are the real
/// Claude Code stdin payload paths (plus <c>git.branch</c>, which Perch injects), so a template reads
/// the same field names the data explorer lists.</summary>
internal static class StatuslineDefaults
{
    public const string PerchDefaultName = "Perch Default";

    public static IReadOnlyList<StatuslineProfile> All => new[]
    {
        new StatuslineProfile
        {
            // The "\\\n" pairs are SOFT breaks (Shift+Enter in the designer): they wrap the editor into
            // readable chunks but are stripped before rendering, so the status line stays one line. Each
            // chunk = one logical piece.
            Name = PerchDefaultName,
            Kind = ProfileKind.Perch,
            Template =
                "{{model.display_name}}  \\\n" +
                "{{#if git.branch}}⎇ {{git.branch}}  {{/if}}\\\n" +
                "ctx {{context_window.used_percentage|bar:8|color:teal}} {{context_window.used_percentage|pct}}  \\\n" +
                "${{cost.total_cost_usd|money}}\\\n" +
                "{{#if pr.number}}  PR#{{pr.number}} {{pr.review_state|color:yellow}}{{/if}}",
        },
        new StatuslineProfile
        {
            Name = "Minimal",
            Kind = ProfileKind.Perch,
            Template =
                "{{model.display_name}} · \\\n" +
                "{{context_window.used_percentage|pct}} · \\\n" +
                "${{cost.total_cost_usd|money}}",
        },
        new StatuslineProfile
        {
            Name = "Two-line Pro",
            Kind = ProfileKind.Perch,
            Template =
                "{{model.display_name|color:teal}}  \\\n" +
                "effort {{effort.level|upper}}  \\\n" +
                "{{#if git.branch}}⎇ {{git.branch}}{{/if}}\n" +
                "ctx {{context_window.used_percentage|bar:12|color:amber}} {{context_window.used_percentage|pct}}   \\\n" +
                "cache {{#if prompt_cache.warm}}warm {{prompt_cache.ttl}}{{/if}}   \\\n" +
                "5h {{rate_limits.five_hour.used_percentage|round}}%",
        },
        new StatuslineProfile
        {
            // A three-line, information-dense line modelled on a classic jq/bash statusline: path + branch,
            // then model·effort with token counts and session duration, then the context bar with the 5h/7d
            // rate-limit windows. The rate-limit readings are PACE-COLOURED — green when behind the expected
            // burn for how far through the window you are, yellow around pace, red over — the same rule the
            // overlay's usage bars use. Context has no reset/window, so it has no expected rate and stays a
            // steady green.
            Name = "Rate-aware verbose",
            Kind = ProfileKind.Perch,
            Template =
                "📁 {{workspace.current_dir|color:green}}\\\n" +
                "{{#if git.branch}}  🍃 {{git.branch|color:green}}\\\n" +
                "{{#if git.dirty}}  (+{{git.staged|color:muted}},-{{git.unstaged|color:muted}}){{/if}}{{/if}}\n" +
                "[{{model.display_name}}{{#if effort.level}} | {{effort.level}}{{/if}}]  \\\n" +
                "🔽 {{context_window.total_input_tokens|human}}  🔼 {{context_window.total_output_tokens|human}}  \\\n" +
                "⏱ {{cost.total_duration_ms|dur}}\n" +
                "Context {{context_window.used_percentage|bar:16|color:green}} \\\n" +
                "{{context_window.total_input_tokens|human}}/{{context_window.context_window_size|human}} \\\n" +
                "({{context_window.used_percentage|round}}%)\\\n" +
                "{{#if rate_limits.five_hour}}  |  5h \\\n" +
                "{{rate_limits.five_hour.used_percentage|bar:10|pace:rate_limits.five_hour.resets_at:18000}} \\\n" +
                "{{rate_limits.five_hour.used_percentage|round|pace:rate_limits.five_hour.resets_at:18000}}% \\\n" +
                "{{rate_limits.five_hour.resets_at|until}}{{/if}}\\\n" +
                "{{#if rate_limits.seven_day}}  |  7d \\\n" +
                "{{rate_limits.seven_day.used_percentage|bar:10|pace:rate_limits.seven_day.resets_at:604800}} \\\n" +
                "{{rate_limits.seven_day.used_percentage|round|pace:rate_limits.seven_day.resets_at:604800}}% \\\n" +
                "{{rate_limits.seven_day.resets_at|until}}{{/if}}",
        },
    };
}

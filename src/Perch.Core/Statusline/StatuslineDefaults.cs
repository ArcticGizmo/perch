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
            // FORMATTING CONVENTION for every example: the "\\\n" pairs are SOFT breaks (Shift+Enter in the
            // designer) — stripped before rendering, so they only shape the EDITOR, never the output. Each
            // logical chunk gets its own line, and each block conditional is laid out like code: the
            // "{{#if …}}" opens a line (with its inline lead-in) and the matching "{{/if}}" sits on its OWN
            // line. Small inline conditionals (e.g. inside "[ … ]") stay inline. Real "\n" = an output row.
            Name = PerchDefaultName,
            Kind = ProfileKind.Perch,
            Template =
                "{{model.display_name}}  \\\n" +
                "{{#if git.branch}}⎇ {{git.branch}}  \\\n" +
                "{{/if}}\\\n" +
                "ctx {{context_window.used_percentage|bar:8|color:teal}} {{context_window.used_percentage|pct}}  \\\n" +
                "${{cost.total_cost_usd|money}}\\\n" +
                "{{#if pr.number}}  PR#{{pr.number}} {{pr.review_state|color:yellow}}\\\n" +
                "{{/if}}",
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
                "{{#if git.branch}}⎇ {{git.branch}}\\\n" +
                "{{/if}}\n" +
                "ctx {{context_window.used_percentage|bar:12|color:amber}} {{context_window.used_percentage|pct}}   \\\n" +
                "cache {{#if prompt_cache.warm}}warm {{prompt_cache.ttl}}\\\n" +
                "{{/if}}   \\\n" +
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
                "{{#if git.dirty}}  (+{{git.staged|color:muted}},-{{git.unstaged|color:muted}})\\\n" +
                "{{/if}}\\\n" +
                "{{/if}}\n" +
                "[{{model.display_name}}{{#if effort.level}} | {{effort.level}}{{/if}}]  \\\n" +
                "🔽 {{context_window.total_input_tokens|human}}  🔼 {{context_window.total_output_tokens|human}}  \\\n" +
                "⏱ {{cost.total_duration_ms|dur}}\n" +
                "Context \\\n" +
                "{{context_window.used_percentage|bar:16|color:green}} {{context_window.total_input_tokens|human}}/{{context_window.context_window_size|human}} ({{context_window.used_percentage|round}}%)\\\n" +
                "{{#if rate_limits.five_hour}}  |  5h {{rate_limits.five_hour.used_percentage|bar:10|pace:rate_limits.five_hour.resets_at:18000}} {{rate_limits.five_hour.used_percentage|round|pace:rate_limits.five_hour.resets_at:18000}}% {{rate_limits.five_hour.resets_at|until}}\\\n" +
                "{{/if}}\\\n" +
                "{{#if rate_limits.seven_day}}  |  7d {{rate_limits.seven_day.used_percentage|bar:10|pace:rate_limits.seven_day.resets_at:604800}} {{rate_limits.seven_day.used_percentage|round|pace:rate_limits.seven_day.resets_at:604800}}% {{rate_limits.seven_day.resets_at|until}}\\\n" +
                "{{/if}}",
        },
    };
}

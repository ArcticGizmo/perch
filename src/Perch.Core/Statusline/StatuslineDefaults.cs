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
            // A tuned take on "Rate-aware verbose": a " | " lead-in before the branch, a "·" between model and
            // effort, a slightly narrower (10-cell) context bar, and muted reset times on the rate-limit
            // windows. Same three rows — path+branch, model·effort with token counts and duration, then the
            // context bar plus the pace-coloured 5h/7d windows.
            Name = "Rate-aware verbose",
            Kind = ProfileKind.Perch,
            Template =
                "📁 {{workspace.current_dir|color:green}}\\\n" +
                "{{#if git.branch}} | 🍃 {{git.branch|color:green}}\\\n" +
                "{{#if git.dirty}}  (+{{git.staged|color:muted}},-{{git.unstaged|color:muted}})\\\n" +
                "{{/if}}\\\n" +
                "{{/if}}\n" +
                "[{{model.display_name}}{{#if effort.level}} · {{effort.level}}{{/if}}]  🔽 {{context_window.total_input_tokens|human}}  🔼 {{context_window.total_output_tokens|human}}  ⏱ {{cost.total_duration_ms|dur}}\n" +
                "Context \\\n" +
                "{{context_window.used_percentage|bar:10|color:green}} {{context_window.total_input_tokens|human}}/{{context_window.context_window_size|human}} ({{context_window.used_percentage|round}}%)\\\n" +
                "{{#if rate_limits.five_hour}} | 5h {{rate_limits.five_hour.used_percentage|bar:10|pace:rate_limits.five_hour.resets_at:18000}} {{rate_limits.five_hour.used_percentage|round|pace:rate_limits.five_hour.resets_at:18000}}% {{rate_limits.five_hour.resets_at|until|color:muted}}\\\n" +
                "{{/if}}\\\n" +
                "{{#if rate_limits.seven_day}} | 7d {{rate_limits.seven_day.used_percentage|bar:10|pace:rate_limits.seven_day.resets_at:604800}} {{rate_limits.seven_day.used_percentage|round|pace:rate_limits.seven_day.resets_at:604800}}% {{rate_limits.seven_day.resets_at|until|color:muted}}\\\n" +
                "{{/if}}",
        },
        new StatuslineProfile
        {
            // Leans on Perch's OWN config: the context bar auto-colours to your configured context-pressure
            // bands via |ctxcolor (green → yellow → amber → red), with an {{else}} for a fresh session where
            // context is still null; the signed-in org is shown; and — the headline — an account guardrail
            // mismatch (you're on the wrong Claude account for this folder) shouts on a red {{#bg:red}}
            // background. All of it degrades gracefully: with no Perch config the bar still colours off the
            // shipped 50/65/80 defaults and the guardrail simply never fires.
            Name = "Account-aware",
            Kind = ProfileKind.Perch,
            Template =
                "{{model.display_name}}  \\\n" +
                "ctx {{#if context_window.used_percentage}}\\\n" +
                "{{context_window.used_percentage|bar:8|ctxcolor}} {{context_window.used_percentage|pct|ctxcolor}}\\\n" +
                "{{else}}(fresh){{/if}}  \\\n" +
                "{{#if account.org}}⌾ {{account.org|trunc:18|color:muted}}\\\n" +
                "{{/if}}\\\n" +
                "{{#if perch.guardrail.mismatch}}{{#bg:red}} ⚠ {{perch.guardrail.expected}} ≠ {{perch.guardrail.on}} {{/bg}}\\\n" +
                "{{/if}}",
        },
    };
}

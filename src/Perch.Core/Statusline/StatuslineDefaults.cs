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
            Name = PerchDefaultName,
            Kind = ProfileKind.Perch,
            Template =
                "{{model.display_name}}  " +
                "{{#if git.branch}}⎇ {{git.branch}}  {{/if}}" +
                "ctx {{context_window.used_percentage|bar:8|color:teal}} {{context_window.used_percentage|pct}}  " +
                "${{cost.total_cost_usd|money}}" +
                "{{#if pr.number}}  PR#{{pr.number}} {{pr.review_state|color:yellow}}{{/if}}",
        },
        new StatuslineProfile
        {
            Name = "Minimal",
            Kind = ProfileKind.Perch,
            Template =
                "{{model.display_name}} · {{context_window.used_percentage|pct}} · ${{cost.total_cost_usd|money}}",
        },
        new StatuslineProfile
        {
            Name = "Two-line Pro",
            Kind = ProfileKind.Perch,
            Template =
                "{{model.display_name|color:teal}}  effort {{effort.level|upper}}  " +
                "{{#if git.branch}}⎇ {{git.branch}}{{/if}}\n" +
                "ctx {{context_window.used_percentage|bar:12|color:amber}} {{context_window.used_percentage|pct}}   " +
                "cache {{#if prompt_cache.warm}}warm {{prompt_cache.ttl}}{{/if}}   " +
                "5h {{rate_limits.five_hour.used_percentage|round}}%",
        },
    };
}

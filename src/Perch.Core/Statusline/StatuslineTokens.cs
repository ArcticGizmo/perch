namespace Perch.Statusline;

using System.Collections.Generic;

/// <summary>A note attached to a token in the data explorer — a caveat the author should know before
/// leaning on the field.</summary>
internal enum TokenBadge
{
    /// <summary>Can be JSON null early in a session (or after <c>/compact</c>) even though the key is
    /// present — guard with <c>{{#if …}}</c> or <c>| default:…</c>.</summary>
    NullEarly,
    /// <summary>Only appears on recent Claude Code versions.</summary>
    VersionGated,
    /// <summary>Not in the raw payload — Perch injects it.</summary>
    PerchExtra,
    /// <summary>Absent unless the relevant state exists (an open PR, vim mode on, …).</summary>
    WhenPresent,
}

/// <summary>One field the designer offers for insertion: its dotted <see cref="Path"/> and any
/// <see cref="Badges"/>. The live sample value is looked up from <see cref="StatuslineSample"/> at
/// display time, so the catalogue never carries a stale copy.</summary>
internal sealed record TokenDescriptor(string Path, params TokenBadge[] Badges);

/// <summary>The grouped catalogue of statusline tokens shown in the designer's data explorer. Grouping
/// and order match the payload's own shape. A test pins that every path here resolves against the
/// sample payload, so the catalogue and the sample can't drift apart.</summary>
internal static class StatuslineTokens
{
    public sealed record Group(string Name, IReadOnlyList<TokenDescriptor> Tokens);

    public static readonly IReadOnlyList<Group> Groups = new[]
    {
        new Group("model", new[]
        {
            new TokenDescriptor("model.display_name"),
            new TokenDescriptor("model.id"),
        }),
        new Group("version", new[]
        {
            new TokenDescriptor("version"),
            new TokenDescriptor("output_style.name"),
        }),
        new Group("workspace", new[]
        {
            new TokenDescriptor("workspace.current_dir"),
            new TokenDescriptor("workspace.repo.owner"),
            new TokenDescriptor("workspace.repo.name"),
        }),
        new Group("cost", new[]
        {
            new TokenDescriptor("cost.total_cost_usd"),
            new TokenDescriptor("cost.total_lines_added"),
            new TokenDescriptor("cost.total_lines_removed"),
            new TokenDescriptor("cost.total_duration_ms"),
        }),
        new Group("context_window", new[]
        {
            new TokenDescriptor("context_window.used_percentage", TokenBadge.NullEarly),
            new TokenDescriptor("context_window.remaining_percentage", TokenBadge.NullEarly),
            new TokenDescriptor("context_window.context_window_size"),
            new TokenDescriptor("context_window.total_input_tokens"),
        }),
        new Group("prompt_cache", new[]
        {
            new TokenDescriptor("prompt_cache.warm", TokenBadge.VersionGated),
            new TokenDescriptor("prompt_cache.hit_ratio", TokenBadge.VersionGated),
            new TokenDescriptor("prompt_cache.ttl", TokenBadge.VersionGated),
        }),
        new Group("rate_limits", new[]
        {
            new TokenDescriptor("rate_limits.five_hour.used_percentage", TokenBadge.WhenPresent),
            new TokenDescriptor("rate_limits.five_hour.resets_at", TokenBadge.WhenPresent),
            new TokenDescriptor("rate_limits.seven_day.used_percentage", TokenBadge.WhenPresent),
            new TokenDescriptor("rate_limits.seven_day.resets_at", TokenBadge.WhenPresent),
        }),
        new Group("pr", new[]
        {
            new TokenDescriptor("pr.number", TokenBadge.WhenPresent),
            new TokenDescriptor("pr.review_state", TokenBadge.WhenPresent),
            new TokenDescriptor("pr.url", TokenBadge.WhenPresent),
        }),
        new Group("effort / vim", new[]
        {
            new TokenDescriptor("effort.level"),
            new TokenDescriptor("vim.mode", TokenBadge.WhenPresent),
        }),
        new Group("git · Perch extras", new[]
        {
            new TokenDescriptor("git.branch", TokenBadge.PerchExtra),
            new TokenDescriptor("git.staged", TokenBadge.PerchExtra),
            new TokenDescriptor("git.unstaged", TokenBadge.PerchExtra),
            new TokenDescriptor("git.changes", TokenBadge.PerchExtra),
            new TokenDescriptor("git.dirty", TokenBadge.PerchExtra),
        }),
        new Group("account · Perch extras", new[]
        {
            // Not in Claude Code's payload — Perch injects these from the session's config-dir
            // .claude.json (oauthAccount). WhenPresent: absent/blank when that dir is signed out.
            new TokenDescriptor("account.org", TokenBadge.PerchExtra, TokenBadge.WhenPresent),
            new TokenDescriptor("account.email", TokenBadge.PerchExtra, TokenBadge.WhenPresent),
            new TokenDescriptor("account.signed_in", TokenBadge.PerchExtra),
            new TokenDescriptor("account.personal", TokenBadge.PerchExtra),
            new TokenDescriptor("account.org_uuid", TokenBadge.PerchExtra, TokenBadge.WhenPresent),
        }),
        new Group("perch config · context & guardrails", new[]
        {
            // Not in Claude Code's payload — Perch injects these by reading its OWN settings.json (the
            // context-pressure thresholds + account guardrails). All degrade to sensible defaults when
            // Perch's config can't be found, so a line stays portable. Pair the thresholds with the
            // |ctxcolor filter to auto-colour a context bar; guardrail.mismatch flags a wrong-account session.
            new TokenDescriptor("perch.context.yellow", TokenBadge.PerchExtra),
            new TokenDescriptor("perch.context.orange", TokenBadge.PerchExtra),
            new TokenDescriptor("perch.context.red", TokenBadge.PerchExtra),
            new TokenDescriptor("perch.guardrail.mismatch", TokenBadge.PerchExtra),
            new TokenDescriptor("perch.guardrail.expected", TokenBadge.PerchExtra, TokenBadge.WhenPresent),
            new TokenDescriptor("perch.guardrail.on", TokenBadge.PerchExtra, TokenBadge.WhenPresent),
        }),
    };
}

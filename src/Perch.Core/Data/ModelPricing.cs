namespace Perch.Data;

/// <summary>A model's pay-as-you-go API price: USD per million input and output tokens, and what a cache read
/// costs as a share of the input price (no longer a flat 0.1× — the newest models discount reads harder).</summary>
internal readonly record struct ModelPrice(decimal InputPerMtok, decimal OutputPerMtok, decimal CacheReadMultiplier)
{
    public decimal CacheReadPerMtok => InputPerMtok * CacheReadMultiplier;
}

/// <summary>
/// The one per-model price table, shared by the Stats cost (<see cref="SessionStatsService.CostOf"/>) and the
/// pre-resume estimate (<c>ResumeEstimate</c>) so the two can't drift apart. Equivalent API prices, not a bill:
/// a subscription doesn't charge per token, but these are the honest relative weights of each token class.
/// </summary>
internal static class ModelPricing
{
    /// <summary>A cache write with the default 5-minute TTL costs 1.25× the input price.</summary>
    public const decimal CacheWrite5mMultiplier = 1.25m;

    /// <summary>A cache write with the 1-hour TTL costs 2× the input price. Claude Code writes its prompt cache
    /// with this TTL (every write in the transcripts checked was <c>ephemeral_1h</c>), so it is also what a cold
    /// resume pays.</summary>
    public const decimal CacheWrite1hMultiplier = 2.0m;

    // Most specific key first. Keys are matched against a normalised name (lower case, spaces and dots turned
    // into hyphens), so a model id ("claude-opus-5-5", "claude-haiku-4-5-20251001", "claude-opus-5-5[1m]"), a
    // display name ("Opus 5.5") and an alias ("opus") all resolve.
    private static readonly (string key, ModelPrice price)[] Table =
    [
        ("fable-5-1",  new(10m, 50m, 0.025m)),
        ("mythos-5-1", new(10m, 50m, 0.025m)),
        ("fable",      new(10m, 50m, 0.10m)),
        ("opus-5-5",   new( 4m, 20m, 0.05m)),
        ("opus",       new( 5m, 25m, 0.10m)),   // Opus 5 and the 4.5–4.8 line share Opus-tier pricing
        ("sonnet-5",   new( 2m, 10m, 0.10m)),   // Sonnet 5 and 5.5
        ("sonnet",     new( 3m, 15m, 0.10m)),
        ("haiku",      new( 1m,  5m, 0.10m)),
    ];

    /// <summary>The price for a model, or null when it isn't recognised — callers show no figure rather than a
    /// fabricated one.</summary>
    public static ModelPrice? For(string? model)
    {
        if (string.IsNullOrEmpty(model)) return null;
        var m = model.ToLowerInvariant().Replace(' ', '-').Replace('.', '-');
        foreach (var (key, price) in Table)
            if (m.Contains(key, StringComparison.Ordinal))
                return price;
        return null;
    }
}

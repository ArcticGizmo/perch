namespace Perch.Data.Control;

/// <summary>How likely a resumed session's prompt cache is still warm, inferred from how long the session has
/// been idle. A warm cache serves the whole prior context at the model's cache-read price (≤0.1× input); a cold
/// one re-writes it at 2× (a 1-hour cache write) on the first message — the difference this whole estimate
/// exists to surface.</summary>
internal enum CacheWarmth
{
    /// <summary>No activity timestamp — can't tell.</summary>
    Unknown,
    /// <summary>Idle well inside Claude Code's 1-hour prompt-cache TTL: the first message hits the cache.</summary>
    Warm,
    /// <summary>Close to the hour: a cache hit is possible, not assured.</summary>
    Cooling,
    /// <summary>Idle beyond an hour: the cache has lapsed — the first message re-writes the whole context.</summary>
    Cold,
}

/// <summary>
/// A pre-resume estimate for a session on disk: how much context resuming will re-send, whether the prompt
/// cache is likely to absorb it, and what that costs — a dollar figure and a (deliberately rough) share of a
/// 5-hour window. Pure and deterministic (<see cref="Compute"/>), so it is unit-testable and the UI just
/// formats it. Every figure is an estimate: the token count is the last prompt the session sent, and the
/// cache/cost numbers assume the first resumed message re-establishes that context.
/// </summary>
internal readonly record struct ResumeEstimate(
    long ContextTokens,
    int WindowTokens,
    string? Model,
    ContextWindowSource WindowSource,
    CacheWarmth Warmth,
    TimeSpan Idle,
    decimal? ColdCostUsd,
    decimal? WarmCostUsd,
    double FiveHourPercent,
    long AssumedFiveHourBudget)
{
    public bool HasData => ContextTokens > 0;

    /// <summary>Context occupancy as a share of the model's window (0–100).</summary>
    public double ContextPercent =>
        WindowTokens > 0 ? Math.Clamp((double)ContextTokens / WindowTokens * 100.0, 0, 100) : 0;

    /// <summary>The estimate that matters given the current warmth: the cold first-message cost when the
    /// cache has (probably) lapsed, else the warm cache-read cost.</summary>
    public decimal? LikelyCostUsd => Warmth == CacheWarmth.Warm ? WarmCostUsd : ColdCostUsd;

    // Claude Code writes its prompt cache with the 1-hour TTL (checked against real transcripts: resumes up to
    // ~61 min idle were all cache reads, from ~63 min they were full re-writes). Idle is measured from the
    // transcript's last write, which trails the last request's start (where the TTL clock begins) by that
    // turn's duration — so the last ten minutes before the hour are only a maybe.
    private static readonly TimeSpan WarmWindow = TimeSpan.FromMinutes(50);
    private static readonly TimeSpan CoolingWindow = TimeSpan.FromMinutes(60);

    // The cache-read weight for an unpriced model's 5h share: the typical read rate.
    private const decimal FallbackCacheReadMultiplier = 0.10m;

    /// <summary>
    /// Assumed input-token allowance of a 5-hour usage window. There is <b>no real figure to use here</b>:
    /// Anthropic's /usage endpoint reports the 5-hour window only as a percentage, with no token cap behind
    /// it, and the true allowance is dynamic and unpublished. This constant only lets the UI show a
    /// consistent, relative "theoretical %" between sessions — treat the number as a rough gauge, not a
    /// billing fact. One place to tune if a better basis ever appears.
    /// </summary>
    public const long AssumedFiveHourInputTokens = 5_000_000;

    /// <summary>Builds the estimate for a session from its context occupancy, resolved window, and how long
    /// it has been idle since its transcript last changed.</summary>
    public static ResumeEstimate Compute(long contextTokens, ContextWindowInfo window, TimeSpan idle)
    {
        var warmth = idle < TimeSpan.Zero ? CacheWarmth.Unknown
                   : idle <= WarmWindow ? CacheWarmth.Warm
                   : idle <= CoolingWindow ? CacheWarmth.Cooling
                   : CacheWarmth.Cold;

        // Cold: the first message re-writes the context into the 1-hour cache (2× input). Warm: it reads it back
        // at the model's cache-read price. Prices come from the same table as the Stats cost.
        var price = ModelPricing.For(window.Model);
        decimal? cold = price is { } p ? contextTokens * p.InputPerMtok * ModelPricing.CacheWrite1hMultiplier / 1_000_000m : null;
        decimal? warm = price is { } q ? contextTokens * q.CacheReadPerMtok / 1_000_000m : null;

        // The 5h share weighs the context the way it is billed — a warm resume costs a sliver of a cold one —
        // in input-token equivalents against the assumed budget. Same likely case as LikelyCostUsd.
        decimal weight = warmth == CacheWarmth.Warm
            ? price?.CacheReadMultiplier ?? FallbackCacheReadMultiplier
            : ModelPricing.CacheWrite1hMultiplier;
        double fivePct = AssumedFiveHourInputTokens > 0
            ? (double)(contextTokens * weight) / AssumedFiveHourInputTokens * 100.0
            : 0;

        return new ResumeEstimate(
            contextTokens, window.Tokens, window.Model, window.Source, warmth, idle, cold, warm,
            fivePct, AssumedFiveHourInputTokens);
    }
}

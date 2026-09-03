namespace Perch.Data.Control;

/// <summary>How likely a resumed session's prompt cache is still warm, inferred from how long the session has
/// been idle. A warm cache serves the whole prior context at ~0.1× input price; a cold one re-bills it at
/// ~1.25× (cache write) on the first message — the difference this whole estimate exists to surface.</summary>
internal enum CacheWarmth
{
    /// <summary>No activity timestamp — can't tell.</summary>
    Unknown,
    /// <summary>Idle within the default prompt-cache TTL (~5 min): the first message likely hits the cache.</summary>
    Warm,
    /// <summary>Past the 5-min TTL but within the extended hour: a cache hit is possible, not assured.</summary>
    Cooling,
    /// <summary>Idle beyond an hour: the cache has certainly lapsed — the first message re-reads the whole
    /// context at full price.</summary>
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

    // Prompt-cache lifetimes: the default breakpoint TTL is 5 minutes; the extended option lasts an hour.
    // Inside 5 min a hit is likely, 5–60 min is a maybe, beyond an hour it has certainly gone cold.
    private static readonly TimeSpan WarmWindow = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan CoolingWindow = TimeSpan.FromMinutes(60);

    // Cost multipliers over the base input price, mirroring SessionStatsService.CostOf: a cold resume writes
    // the context into the cache (~1.25×) on the first message; a warm one reads it back (~0.1×).
    private const decimal CacheWriteMultiplier = 1.25m;
    private const decimal CacheReadMultiplier = 0.10m;

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

        decimal? inPrice = InputPricePerMtok(window.Model);
        decimal? cold = inPrice is { } p ? contextTokens * p * CacheWriteMultiplier / 1_000_000m : null;
        decimal? warm = inPrice is { } q ? contextTokens * q * CacheReadMultiplier / 1_000_000m : null;

        double fivePct = AssumedFiveHourInputTokens > 0
            ? (double)contextTokens / AssumedFiveHourInputTokens * 100.0
            : 0;

        return new ResumeEstimate(
            contextTokens, window.Tokens, window.Model, window.Source, warmth, idle, cold, warm,
            fivePct, AssumedFiveHourInputTokens);
    }

    /// <summary>Base input price (USD per million tokens) for a model, matched by family so it works on an id
    /// ("claude-opus-4-8"), a display name ("Opus 4.8"), or an alias ("opus"). Null for an unknown model —
    /// the caller shows no dollar figure rather than a fabricated one. Mirrors SessionStatsService.Prices.</summary>
    private static decimal? InputPricePerMtok(string? model)
    {
        if (string.IsNullOrEmpty(model)) return null;
        var m = model.ToLowerInvariant();
        if (m.Contains("fable")) return 10m;
        if (m.Contains("opus")) return 5m;
        if (m.Contains("sonnet")) return 3m;
        if (m.Contains("haiku")) return 1m;
        return null;
    }
}

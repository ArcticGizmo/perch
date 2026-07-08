namespace Perch.Data;

/// <summary>The personality archetype a Wrapped poster celebrates. Derived from the shape of the
/// stats (when you work, how much you delegate, how tool-heavy you are) rather than raw size — so two
/// people with the same token count can still get different cards. See <see cref="WrappedSummary.Build"/>.</summary>
internal enum WrappedPersona
{
    Builder,
    NightOwl,
    EarlyBird,
    ToolWhisperer,
    Marathoner,
    AgentWrangler,
    TokenTitan,
    Conversationalist,
}

/// <summary>A single playful line — an emoji and its text kept apart so the renderer can draw the
/// emoji in a colour-emoji font and the text in the body font (mixing them in one GDI+ string renders
/// the emoji as a tofu box).</summary>
internal sealed record WrappedItem(string Emoji, string Text);

/// <summary>
/// A "Perch Wrapped" — a Spotify-Wrapped-style snapshot of one scope's stats, distilled into a fun,
/// shareable poster. This is the pure-data half (no drawing): it picks a persona, builds the playful
/// equivalences ("≈ 3 novels of text") and the highlight one-liners, and carries the headline numbers.
/// The app's owner-drawn <c>WrappedPoster</c> control turns it into a shareable image.
///
/// Everything here is derived from a <see cref="StatsReport"/> (and the optional <see cref="RangeReport"/>
/// for the range scopes), so it's retroactive and testable without touching the UI.
/// </summary>
internal sealed record WrappedSummary(
    string ScopeTitle,                       // "ALL TIME", "THIS WEEK", …
    string Subtitle,                         // date range / "since Mar 2025"
    WrappedPersona Persona,
    string PersonaName,
    string PersonaEmoji,
    string PersonaTagline,
    int Sessions,
    TimeSpan ActiveTime,
    int Prompts,
    int ToolCalls,
    int SubAgents,
    long TotalTokens,
    decimal EstimatedCost,
    bool ShowCost,
    string? TopProject,
    string? TopTool,
    string? TopModel,
    int? PeakHour,                           // 0-23 local hour of greatest activity, null if none
    IReadOnlyList<WrappedItem> Highlights,   // playful one-liners (emoji + text)
    IReadOnlyList<WrappedItem> Equivalences) // "≈ 3 novels of text", "≈ 5 movies of focus", …
{
    /// <summary>Builds the Wrapped from a computed report. <paramref name="range"/> is non-null for the
    /// 7-day / 30-day / all-time scopes and unlocks the streak / busiest-day / longest-session extras.</summary>
    public static WrappedSummary Build(StatsReport report, RangeReport? range,
        string scopeTitle, string subtitle, bool showCost)
    {
        int peakHour = PeakHourOf(report.HourlyActiveSeconds);
        bool hasPeak = peakHour >= 0;

        double toolsPerPrompt = report.Prompts > 0 ? report.ToolCalls / (double)report.Prompts : report.ToolCalls;
        double avgSessionMin = report.SessionCount > 0
            ? report.ActiveTime.TotalMinutes / report.SessionCount : 0;
        TimeSpan longest = range?.LongestSession ?? TimeSpan.Zero;

        var persona = PickPersona(report, range, hasPeak, peakHour, toolsPerPrompt, avgSessionMin, longest);
        var (name, emoji, tagline) = Describe(persona);

        return new WrappedSummary(
            scopeTitle, subtitle, persona, name, emoji, tagline,
            report.SessionCount, report.ActiveTime, report.Prompts, report.ToolCalls, report.SubAgents,
            report.Tokens.Total, report.EstimatedCost, showCost && report.EstimatedCost > 0,
            report.Projects.Count > 0 ? report.Projects[0].Project : null,
            report.Tools.Count > 0 ? report.Tools[0].Tool : null,
            report.Models.Count > 0 ? PrettyModel(report.Models[0].Model) : null,
            hasPeak ? peakHour : null,
            BuildHighlights(report, range, hasPeak ? peakHour : null),
            BuildEquivalences(report));
    }

    // Pick the most distinctive archetype. Ordered by how strong a signal each behaviour is: heavy
    // delegation and tool-spamming are loud, deliberate signals; the time-of-day personas are the
    // friendly fallback that nearly everyone qualifies for.
    private static WrappedPersona PickPersona(StatsReport r, RangeReport? range, bool hasPeak, int peakHour,
        double toolsPerPrompt, double avgSessionMin, TimeSpan longest)
    {
        if (r.SubAgents >= 8)
            return WrappedPersona.AgentWrangler;
        if (r.ToolCalls >= 30 && toolsPerPrompt >= 10)
            return WrappedPersona.ToolWhisperer;
        if (longest >= TimeSpan.FromHours(2) || avgSessionMin >= 45)
            return WrappedPersona.Marathoner;
        if (hasPeak && (peakHour >= 22 || peakHour <= 4))
            return WrappedPersona.NightOwl;
        if (hasPeak && peakHour is >= 5 and <= 8)
            return WrappedPersona.EarlyBird;
        if (r.Tokens.Total >= 20_000_000)
            return WrappedPersona.TokenTitan;
        if (r.Prompts >= 50)
            return WrappedPersona.Conversationalist;
        return WrappedPersona.Builder;
    }

    private static (string name, string emoji, string tagline) Describe(WrappedPersona p) => p switch
    {
        WrappedPersona.NightOwl         => ("Night Owl",          "🦉", "You and the moon ship code together."),
        WrappedPersona.EarlyBird        => ("Early Bird",         "🌅", "First light, first commit."),
        WrappedPersona.ToolWhisperer    => ("Tool Whisperer",     "🛠️", "More action than chatter — Claude does your bidding."),
        WrappedPersona.Marathoner       => ("The Marathoner",     "🏃", "Deep work is your natural habitat."),
        WrappedPersona.AgentWrangler    => ("Agent Wrangler",     "🤖", "Why do it solo? You command a fleet."),
        WrappedPersona.TokenTitan       => ("Token Titan",        "💎", "You think in millions of tokens."),
        WrappedPersona.Conversationalist=> ("The Conversationalist","💬","You and Claude never run out of things to say."),
        _                               => ("The Builder",        "🐦", "Perched, focused, and shipping."),
    };

    // The fun "that's about N of something" lines. Conservative: only emit a unit once the count rounds
    // to at least one, so we never claim "≈ 0 novels".
    private static List<WrappedItem> BuildEquivalences(StatsReport r)
    {
        var list = new List<WrappedItem>();

        // ~0.75 words per token; a paperback novel is ~90k words, a page ~500.
        double words = r.Tokens.Total * 0.75;
        int novels = (int)Math.Round(words / 90_000.0);
        int pages = (int)Math.Round(words / 500.0);
        if (novels >= 1)
            list.Add(new("📚", $"≈ {novels} {(novels == 1 ? "novel" : "novels")} of text"));
        else if (pages >= 1)
            list.Add(new("📄", $"≈ {pages} {(pages == 1 ? "page" : "pages")} of text"));

        // A feature film is ~2 hours of focus.
        double hours = r.ActiveTime.TotalHours;
        int movies = (int)Math.Round(hours / 2.0);
        if (movies >= 1)
            list.Add(new("🎬", $"≈ {movies} {(movies == 1 ? "movie" : "movies")} of focus"));

        return list;
    }

    // The highlight reel — up to three punchy facts, the meatier (range-only) ones first. Capped at
    // three so the dense all-time card still clears the footer.
    private static List<WrappedItem> BuildHighlights(StatsReport r, RangeReport? range, int? peakHour)
    {
        var list = new List<WrappedItem>();

        if (range is { } rng)
        {
            if (rng.StreakDays is { } streak && streak >= 2)
                list.Add(new("🔥", $"{streak}-day streak"));
            if (rng.LongestSession >= TimeSpan.FromMinutes(5))
                list.Add(new("⏱️", $"Longest session: {StatsFormat.Duration(rng.LongestSession)}"));
            if (rng.BusiestDay is { } bd)
                list.Add(new("📅", $"Busiest day: {bd:MMM d}"));
            if (rng.ActiveDays > 0)
                list.Add(new("✅", $"{rng.ActiveDays} active {(rng.ActiveDays == 1 ? "day" : "days")}"));
        }

        if (peakHour is { } ph)
            list.Add(new("🌙", $"Peak hour: {HourLabel(ph)}"));
        if (r.SubAgents > 0)
            list.Add(new("🤝", $"{r.SubAgents} sub-agent {(r.SubAgents == 1 ? "run" : "runs")} dispatched"));

        return list.Take(3).ToList();
    }

    private static int PeakHourOf(int[] hourly)
    {
        int best = -1;
        long max = 0;
        for (int h = 0; h < hourly.Length; h++)
            if (hourly[h] > max) { max = hourly[h]; best = h; }
        return best;
    }

    private static string PrettyModel(string model)
    {
        string s = model.StartsWith("claude-", StringComparison.Ordinal) ? model["claude-".Length..] : model;
        // Drop a trailing dated snapshot suffix (e.g. "-20251001") for a cleaner card.
        int dash = s.LastIndexOf('-');
        if (dash > 0 && dash + 1 < s.Length && s[(dash + 1)..].All(char.IsDigit) && s.Length - dash - 1 >= 6)
            s = s[..dash];
        return s;
    }

    // 0 -> "12am", 13 -> "1pm".
    internal static string HourLabel(int h)
    {
        int hr = h % 12;
        if (hr == 0) hr = 12;
        return $"{hr}{(h < 12 ? "am" : "pm")}";
    }
}

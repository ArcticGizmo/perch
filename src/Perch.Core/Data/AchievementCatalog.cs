namespace Perch.Data;

/// <summary>How shiny a badge is — drives ordering and colour in the grid. Bronze is a warm-up, Gold is a
/// genuine feat (or a genuine cry for help).</summary>
internal enum AchievementTier { Bronze, Silver, Gold }

/// <summary>One rung of an achievement — a single threshold with its own name, emoji and tier (levels
/// don't have to share a name). <see cref="Id"/> is the stable unlock key the store tracks, so a toast for
/// reaching this rung fires exactly once.</summary>
internal sealed record AchievementLevel(
    string Id, string Name, string Emoji, string Criteria, AchievementTier Tier, bool Earned);

/// <summary>
/// A family's evaluated state — one tile in the grid. Scaling families (tokens, sessions, streak, …) fold
/// their thresholds into a single levelled tile: <see cref="Level"/> of <see cref="MaxLevel"/> rungs
/// reached, the display fields showing the current rung (or the first rung as the goal when none are), and
/// <see cref="Progress"/> tracking the climb through the current band. <see cref="Category"/> is the small
/// grey label ("Tokens", "Streak") that says what the levels are comparing; it's empty for one-off badges
/// (a single rung, or a condition with no meaningful "how close"). <see cref="Levels"/> carries every rung
/// so the unlock service can tell which were newly reached.
/// </summary>
internal sealed record Achievement(
    string Id, string Category, string Name, string Emoji, string Description, AchievementTier Tier,
    bool Earned, int Level, int MaxLevel, double? Progress, IReadOnlyList<AchievementLevel> Levels);

/// <summary>
/// The Perch achievement catalogue — collectible tray trophies derived from lifetime stats. Every badge is
/// computable from an all-time <see cref="StatsReport"/> (+ the <see cref="RangeReport"/> for streak/record
/// extras), so unlocks are <b>retroactive</b> and need no persisted state of their own — a pure, testable
/// function (mirror <see cref="WrappedSummary"/> and its tests).
///
/// Two shapes fold into the one <see cref="Achievement"/> tile: a <b>levelled</b> family (a metric with an
/// ordered set of rungs, e.g. tokens 1M→10M→…→10B) that levels up in place, and a <b>one-off</b> (a single
/// metric rung with a progress bar, or a bare condition with no bar). This keeps the grid compact — related
/// milestones become one climbing tile instead of a wall of near-duplicates.
/// </summary>
internal static class AchievementCatalog
{
    private static readonly IReadOnlyList<Family> Families = BuildFamilies();

    /// <summary>Evaluates every family against the lifetime stats, one <see cref="Achievement"/> tile each.
    /// <paramref name="range"/> supplies streak/records (null → those read as unearned). With
    /// <paramref name="includeCost"/> false the spend family drops out rather than leak a figure.</summary>
    public static IReadOnlyList<Achievement> Evaluate(StatsReport report, RangeReport? range, bool includeCost)
    {
        var ctx = new Ctx(report, range);
        var list = new List<Achievement>(Families.Count);
        foreach (var fam in Families)
        {
            if (fam.NeedsCost && !includeCost)
                continue;
            list.Add(fam.Evaluate(ctx));
        }
        return list;
    }

    private static List<Family> BuildFamilies()
    {
        const double M = 1_000_000, B = 1_000_000_000;

        return new List<Family>
        {
            // ── Scaling families (level up in place) ──
            Family.Levelled("tokens", "Tokens", c => c.Tokens,
                R("wordsmith",  "Wordsmith",  "✍️", AchievementTier.Bronze, 1 * M,   "1M tokens"),
                R("prolific",   "Prolific",   "📚", AchievementTier.Bronze, 10 * M,  "10M tokens"),
                R("tokenaire",  "Tokenaire",  "🤑", AchievementTier.Silver, 100 * M, "100M tokens"),
                R("tokentitan", "Token Titan","💎", AchievementTier.Gold,   1 * B,   "1B tokens"),
                R("tokenlord",  "Tokenlord",  "👑", AchievementTier.Gold,   10 * B,  "10B tokens")),

            Family.Levelled("sessions", "Sessions", c => c.Sessions,
                R("firstflight",   "First Flight",   "🐣", AchievementTier.Bronze, 1,     "1 session"),
                R("frequentflyer", "Frequent Flyer", "🕊️", AchievementTier.Bronze, 10,    "10 sessions"),
                R("century",       "Century",        "💯", AchievementTier.Silver, 100,   "100 sessions"),
                R("millennium",    "Millennium",     "🏛️", AchievementTier.Gold,   1_000, "1,000 sessions")),

            Family.Levelled("prompts", "Prompts", c => c.Prompts,
                R("conversationalist", "Conversationalist", "💬", AchievementTier.Bronze, 100,      "100 prompts"),
                R("chatterbox",        "Chatterbox",        "🗣️", AchievementTier.Bronze, 1_000,    "1,000 prompts"),
                R("filibuster",        "Filibuster",        "📢", AchievementTier.Silver, 10_000,   "10,000 prompts"),
                R("motormouth",        "Motormouth",        "🎤", AchievementTier.Gold,   100_000,  "100,000 prompts")),

            Family.Levelled("toolcalls", "Tool calls", c => c.ToolCalls,
                R("handy",     "Handy",     "🔧", AchievementTier.Bronze, 1_000,   "1,000 tool calls"),
                R("toolshed",  "Toolshed",  "🧰", AchievementTier.Silver, 10_000,  "10,000 tool calls"),
                R("toolsmith", "Toolsmith", "⚙️", AchievementTier.Gold,   100_000, "100,000 tool calls")),

            Family.Levelled("activetime", "Active time", c => c.ActiveHours,
                R("clockingin",  "Clocking In",     "⏰", AchievementTier.Bronze, 24,    "24 hours active"),
                R("timesink",    "Time Sink",       "🕳️", AchievementTier.Silver, 100,   "100 hours active"),
                R("whatissleep", "What Is Sleep?",  "💀", AchievementTier.Gold,   500,   "500 hours active"),
                R("thousandhr",  "1,000-Hour Club", "🧘", AchievementTier.Gold,   1_000, "1,000 hours active")),

            Family.Levelled("spend", "Spend", c => (double)c.Cost, needsCost: true,
                R("bigspender", "Big Spender", "💸", AchievementTier.Silver, 100,    "$100 equivalent"),
                R("whale",      "Whale",       "🐋", AchievementTier.Gold,   1_000,  "$1,000 equivalent"),
                R("kingpin",    "Kingpin",     "💰", AchievementTier.Gold,   10_000, "$10,000 equivalent")),

            Family.Levelled("streak", "Streak", c => c.Streak,
                R("warmedup",    "Warmed Up",   "🔥", AchievementTier.Bronze, 3,   "3-day streak"),
                R("onfire",      "On Fire",     "🌋", AchievementTier.Silver, 7,   "7-day streak"),
                R("unstoppable", "Unstoppable", "☄️", AchievementTier.Gold,   30,  "30-day streak"),
                R("inferno",     "Inferno",     "🔆", AchievementTier.Gold,   100, "100-day streak")),

            Family.Levelled("activedays", "Active days", c => c.ActiveDays,
                R("regular",         "Regular",           "📆", AchievementTier.Bronze, 30,  "30 active days"),
                R("creatureofhabit", "Creature of Habit", "🗓️", AchievementTier.Silver, 100, "100 active days"),
                R("yearrounder",     "Year-Rounder",      "🎂", AchievementTier.Gold,   365, "365 active days")),

            Family.Levelled("subagents", "Sub-agents", c => c.SubAgents,
                R("delegator",     "Delegator",      "🤝", AchievementTier.Bronze, 1,      "1 sub-agent"),
                R("middlemanager", "Middle Manager", "👔", AchievementTier.Silver, 100,    "100 sub-agents"),
                R("puppetmaster",  "Puppet Master",  "🎭", AchievementTier.Gold,   1_000,  "1,000 sub-agents"),
                R("overlord",      "Overlord",       "🐙", AchievementTier.Gold,   10_000, "10,000 sub-agents")),

            Family.Levelled("longest", "Longest session", c => c.LongestSessionHours,
                R("marathoner",      "Marathoner",      "🏃", AchievementTier.Silver, 2, "2-hour session"),
                R("ultramarathoner", "Ultramarathoner", "🥵", AchievementTier.Gold,   4, "4-hour session"),
                R("ironbutt",        "Iron Butt",       "🪑", AchievementTier.Gold,   8, "8-hour session")),

            Family.Levelled("projects", "Projects", c => c.Projects,
                R("multitasker", "Multitasker", "🗂️", AchievementTier.Bronze, 5,  "5 projects"),
                R("nomad",       "Nomad",       "🧭", AchievementTier.Silver, 15, "15 projects"),
                R("polymath",    "Polymath",    "🌐", AchievementTier.Gold,   40, "40 projects")),

            Family.Levelled("branches", "Branches", c => c.Branches,
                R("branchhopper",  "Branch Hopper",  "🌱", AchievementTier.Bronze, 5,  "5 branches"),
                R("branchmanager", "Branch Manager", "🌿", AchievementTier.Silver, 10, "10 branches"),
                R("arborist",      "Arborist",       "🌳", AchievementTier.Gold,   25, "25 branches")),

            Family.Levelled("teammates", "Teammates", c => c.Teammates,
                R("teamcaptain",  "Team Captain",  "🧢", AchievementTier.Bronze, 1,  "1 teammate"),
                R("fieldmarshal", "Field Marshal", "🎖️", AchievementTier.Gold,   10, "10 teammates")),

            // ── One-off quota badges (single rung + progress bar, no category) ──
            Family.Single("around-the-clock", "Around the Clock", "🕛", "Activity in all 24 hours",
                AchievementTier.Gold, c => c.ActiveHourCount, 24),
            Family.Single("model-citizen", "Model Citizen", "🎛️", "Three model families",
                AchievementTier.Silver, c => c.ModelFamilies, 3),
            Family.Single("jack-of-all-tools", "Jack of All Tools", "🃏", "Eight different tools",
                AchievementTier.Gold, c => c.DistinctTools, 8),
            Family.Single("grep-goblin", "Grep Goblin", "🔍", "500 Grep calls",
                AchievementTier.Silver, c => c.Tool("Grep"), 500),
            Family.Single("bash-brawler", "Bash Brawler", "💥", "500 Bash calls",
                AchievementTier.Silver, c => c.Tool("Bash"), 500),
            Family.Single("the-editor", "The Editor", "✏️", "1,000 Edits",
                AchievementTier.Silver, c => c.Tool("Edit"), 1_000),
            Family.Single("well-read", "Well-Read", "📖", "1,000 Reads",
                AchievementTier.Silver, c => c.Tool("Read"), 1_000),

            // ── One-off conditional badges (no bar) ──
            Family.Conditional("night-owl", "Night Owl", "🦉", "Your busiest hour is after dark",
                AchievementTier.Bronze, c => c.PeakHour is >= 22 or (>= 0 and <= 4)),
            Family.Conditional("early-bird", "Early Bird", "🌅", "Your busiest hour is at first light",
                AchievementTier.Bronze, c => c.PeakHour is >= 5 and <= 8),
            Family.Conditional("the-3am-club", "The 3am Club", "🌚", "Logged work in the 3am hour",
                AchievementTier.Silver, c => c.HourActive(3)),
            Family.Conditional("cache-money", "Cache Money", "🪙", "More cache reads than fresh input",
                AchievementTier.Bronze, c => c.CacheRead > c.Input && c.Input > 0),
            Family.Conditional("one-trick-pony", "One-Trick Pony", "🐴", "One tool is 90% of your calls",
                AchievementTier.Bronze, c => c.ToolCalls >= 100 && c.TopToolShare >= 0.9),
        };
    }

    private static Rung R(string id, string name, string emoji, AchievementTier tier, double target, string criteria) =>
        new(id, name, emoji, tier, target, criteria);

    // One threshold within a family.
    private sealed record Rung(string Id, string Name, string Emoji, AchievementTier Tier, double Target, string Criteria);

    // A family evaluates to one tile. Metric families climb their rungs; conditional families are a single
    // predicate rung with no bar.
    private sealed record Family(
        string Id, string Category, bool NeedsCost,
        Func<Ctx, double>? Metric, IReadOnlyList<Rung> Rungs, Func<Ctx, bool>? Condition)
    {
        public static Family Levelled(string id, string category, Func<Ctx, double> metric, params Rung[] rungs) =>
            new(id, category, false, metric, rungs, null);

        public static Family Levelled(string id, string category, Func<Ctx, double> metric, bool needsCost, params Rung[] rungs) =>
            new(id, category, needsCost, metric, rungs, null);

        // A single-rung metric badge (progress bar, no category — nothing to compare it against).
        public static Family Single(string id, string name, string emoji, string criteria,
            AchievementTier tier, Func<Ctx, double> metric, double target) =>
            new(id, "", false, metric, [new Rung(id, name, emoji, tier, target, criteria)], null);

        // A bare condition — earned or not, no linear "how close", so no bar.
        public static Family Conditional(string id, string name, string emoji, string criteria,
            AchievementTier tier, Func<Ctx, bool> predicate) =>
            new(id, "", false, null, [new Rung(id, name, emoji, tier, 0, criteria)], predicate);

        public Achievement Evaluate(Ctx ctx)
        {
            int max = Rungs.Count;

            // How many rungs are reached, and the metric value (for the progress band).
            int level;
            double value = 0;
            if (Metric is { } metric)
            {
                value = metric(ctx);
                level = 0;
                foreach (var rung in Rungs)
                    if (value >= rung.Target) level++;
            }
            else
            {
                level = Condition!(ctx) ? 1 : 0;
            }

            var levels = new List<AchievementLevel>(max);
            for (int i = 0; i < max; i++)
            {
                var rung = Rungs[i];
                // Namespaced rung id so two families can't collide (matters for the unlock store).
                levels.Add(new AchievementLevel($"{Id}.{rung.Id}", rung.Name, rung.Emoji, rung.Criteria, rung.Tier, i < level));
            }

            bool earned = level >= 1;
            var cur = Rungs[level >= 1 ? level - 1 : 0];   // current rung, or the first as the goal

            // Progress through the current band toward the next rung (metric families only).
            double? progress = null;
            if (Metric is not null && level < max)
            {
                double lower = level == 0 ? 0 : Rungs[level - 1].Target;
                double upper = Rungs[level].Target;
                if (upper > lower)
                    progress = Math.Clamp((value - lower) / (upper - lower), 0, 1);
            }

            string description = level < max
                ? (Category.Length > 0 ? $"Next: {Rungs[level].Criteria}" : Rungs[level].Criteria)
                : (max > 1 ? $"Maxed — {cur.Criteria}" : cur.Criteria);

            return new Achievement(Id, Category, cur.Name, cur.Emoji, description, cur.Tier,
                earned, level, max, progress, levels);
        }
    }

    // Everything the metrics/predicates need, computed once from the report so each family is cheap.
    private sealed class Ctx
    {
        private readonly StatsReport _r;
        private readonly Dictionary<string, int> _tools;

        public Ctx(StatsReport r, RangeReport? range)
        {
            _r = r;
            _tools = r.Tools.ToDictionary(t => t.Tool, t => t.Count, StringComparer.Ordinal);

            int peak = -1; long max = 0;
            int activeHours = 0;
            for (int h = 0; h < r.HourlyActiveSeconds.Length; h++)
            {
                if (r.HourlyActiveSeconds[h] > max) { max = r.HourlyActiveSeconds[h]; peak = h; }
                if (r.HourlyActiveSeconds[h] > 0) activeHours++;
            }
            PeakHour = peak;
            ActiveHourCount = activeHours;

            long toolTotal = r.Tools.Sum(t => (long)t.Count);
            TopToolShare = toolTotal > 0 ? r.Tools.Max(t => (long)t.Count) / (double)toolTotal : 0;
            DistinctTools = r.Tools.Count;

            var families = new HashSet<string>(StringComparer.Ordinal);
            foreach (var m in r.Models)
                foreach (var fam in new[] { "opus", "sonnet", "haiku", "fable" })
                    if (m.Model.Contains(fam, StringComparison.OrdinalIgnoreCase))
                        families.Add(fam);
            ModelFamilies = families.Count;

            Streak = range?.StreakDays ?? 0;
            ActiveDays = range?.ActiveDays ?? 0;
            LongestSessionHours = (range?.LongestSession ?? TimeSpan.Zero).TotalHours;
        }

        public int Sessions => _r.SessionCount;
        public long Tokens => _r.Tokens.Total;
        public long Input => _r.Tokens.Input;
        public long CacheRead => _r.Tokens.CacheRead;
        public int Prompts => _r.Prompts;
        public int ToolCalls => _r.ToolCalls;
        public int SubAgents => _r.SubAgents;
        public int Teammates => _r.Teammates;
        public int Projects => _r.Projects.Count;
        public int Branches => _r.Branches.Count;
        public decimal Cost => _r.EstimatedCost;
        public double ActiveHours => _r.ActiveTime.TotalHours;

        public int PeakHour { get; }
        public int ActiveHourCount { get; }
        public double TopToolShare { get; }
        public int DistinctTools { get; }
        public int ModelFamilies { get; }
        public int Streak { get; }
        public int ActiveDays { get; }
        public double LongestSessionHours { get; }

        public int Tool(string name) => _tools.GetValueOrDefault(name);
        public bool HourActive(int hour) => hour >= 0 && hour < _r.HourlyActiveSeconds.Length && _r.HourlyActiveSeconds[hour] > 0;
    }
}

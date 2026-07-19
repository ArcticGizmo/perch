namespace Perch.Data;

/// <summary>How shiny a badge is — drives ordering and colour in the dashboard grid. Bronze is a warm-up,
/// Gold is a genuine feat (or a genuine cry for help).</summary>
internal enum AchievementTier { Bronze, Silver, Gold }

/// <summary>One evaluated achievement: the badge's identity plus whether the current stats have earned it.
/// Pure data — the dashboard turns a list of these into the owner-drawn trophy grid.</summary>
internal sealed record Achievement(
    string Id, string Name, string Emoji, string Description, AchievementTier Tier, bool Earned);

/// <summary>
/// The Perch achievement catalogue — collectible tray trophies derived from lifetime stats. Every badge
/// here is computable from an all-time <see cref="StatsReport"/> (+ the <see cref="RangeReport"/> for the
/// streak/record extras), so unlocks are <b>retroactive</b>: they light up for history that ran long
/// before the feature existed, and they need no persisted state of their own. That also means this is a
/// pure, testable function — mirror <see cref="WrappedSummary"/> and its tests.
///
/// This is the "free slice": only badges expressible from the aggregate report. Badges that need durable
/// per-unlock state (first-unlocked date, once-only toasts) or live session events are deliberately out
/// of scope until an achievement store exists.
/// </summary>
internal static class AchievementCatalog
{
    // The badge definitions. Ordered loosely by theme; the dashboard re-sorts (earned + shiniest first).
    // Each predicate reads only the precomputed Ctx, so adding a badge is a one-liner and stays testable.
    private static readonly IReadOnlyList<Def> Defs = BuildDefs();

    /// <summary>Evaluates every badge against the lifetime stats. <paramref name="range"/> supplies the
    /// streak/records signals; when null (a non-range scope) those badges simply read as unearned.
    /// <paramref name="includeCost"/> honours the user's "show estimated cost" preference — with cost
    /// hidden, the spend badges drop out of the catalogue entirely rather than leak a dollar figure.</summary>
    public static IReadOnlyList<Achievement> Evaluate(StatsReport report, RangeReport? range, bool includeCost)
    {
        var ctx = new Ctx(report, range);
        var list = new List<Achievement>(Defs.Count);
        foreach (var d in Defs)
        {
            if (d.NeedsCost && !includeCost)
                continue;
            list.Add(new Achievement(d.Id, d.Name, d.Emoji, d.Description, d.Tier, d.Earned(ctx)));
        }
        return list;
    }

    private static List<Def> BuildDefs()
    {
        const long Million = 1_000_000;
        return new List<Def>
        {
            // ── Volume milestones ──
            new("first-flight", "First Flight", "🐣", "Ran your first session.", AchievementTier.Bronze,
                c => c.Sessions >= 1),
            new("century", "Century", "💯", "100 sessions in the books.", AchievementTier.Silver,
                c => c.Sessions >= 100),
            new("millennium", "Millennium", "🏛️", "1,000 sessions — a thousand tiny odysseys.", AchievementTier.Gold,
                c => c.Sessions >= 1000),
            new("wordsmith", "Wordsmith", "✍️", "A million tokens through the pipe.", AchievementTier.Bronze,
                c => c.Tokens >= Million),
            new("token-titan", "Token Titan", "💎", "20 million tokens. You think big.", AchievementTier.Silver,
                c => c.Tokens >= 20 * Million),
            new("tokenaire", "Tokenaire", "🤑", "100 million tokens. Absurd. Magnificent.", AchievementTier.Gold,
                c => c.Tokens >= 100 * Million),
            new("chatterbox", "Chatterbox", "💬", "1,000 prompts sent.", AchievementTier.Bronze,
                c => c.Prompts >= 1000),
            new("filibuster", "Filibuster", "🗣️", "10,000 prompts. Do you ever stop?", AchievementTier.Silver,
                c => c.Prompts >= 10_000),
            new("tool-time", "Tool Time", "🔧", "1,000 tool calls.", AchievementTier.Bronze,
                c => c.ToolCalls >= 1000),
            new("toolshed", "Toolshed", "🏚️", "10,000 tool calls.", AchievementTier.Silver,
                c => c.ToolCalls >= 10_000),

            // ── Time invested ──
            new("clocking-in", "Clocking In", "⏰", "24 hours of active engagement.", AchievementTier.Bronze,
                c => c.ActiveHours >= 24),
            new("time-sink", "Time Sink", "🕳️", "100 hours in. No going back now.", AchievementTier.Silver,
                c => c.ActiveHours >= 100),
            new("what-is-sleep", "What Is Sleep?", "💀", "500 hours active. Perch is a little worried.", AchievementTier.Gold,
                c => c.ActiveHours >= 500),

            // ── Spend (only when the user shows estimated cost) ──
            new("big-spender", "Big Spender", "💸", "≈ $100 in API-equivalent cost.", AchievementTier.Silver, true,
                c => c.Cost >= 100m),
            new("whale", "Whale", "🐋", "≈ $1,000 equivalent. A true patron of the tokens.", AchievementTier.Gold, true,
                c => c.Cost >= 1000m),

            // ── Streaks & consistency ──
            new("warmed-up", "Warmed Up", "🔥", "A 3-day streak.", AchievementTier.Bronze,
                c => c.Streak >= 3),
            new("on-fire", "On Fire", "🌋", "A 7-day streak.", AchievementTier.Silver,
                c => c.Streak >= 7),
            new("unstoppable", "Unstoppable", "☄️", "A 30-day streak. Genuinely unstoppable.", AchievementTier.Gold,
                c => c.Streak >= 30),
            new("regular", "Regular", "📆", "Active on 30 different days.", AchievementTier.Bronze,
                c => c.ActiveDays >= 30),
            new("creature-of-habit", "Creature of Habit", "🗓️", "100 active days.", AchievementTier.Silver,
                c => c.ActiveDays >= 100),

            // ── Time of day ──
            new("night-owl", "Night Owl", "🦉", "Your busiest hour is after dark.", AchievementTier.Bronze,
                c => c.PeakHour is >= 22 or (>= 0 and <= 4)),
            new("early-bird", "Early Bird", "🌅", "Your busiest hour is at first light.", AchievementTier.Bronze,
                c => c.PeakHour is >= 5 and <= 8),
            new("the-3am-club", "The 3am Club", "🌚", "You've logged work in the 3am hour.", AchievementTier.Silver,
                c => c.HourActive(3)),
            new("around-the-clock", "Around the Clock", "🕛", "Activity in all 24 hours of the day.", AchievementTier.Gold,
                c => c.AllHoursActive),

            // ── Session shape ──
            new("marathoner", "Marathoner", "🏃", "A single session over 2 hours.", AchievementTier.Silver,
                c => c.LongestSessionHours >= 2),
            new("ultramarathoner", "Ultramarathoner", "🥵", "A single session over 4 hours.", AchievementTier.Gold,
                c => c.LongestSessionHours >= 4),

            // ── Delegation & the swarm ──
            new("delegator", "Delegator", "🤝", "Dispatched your first sub-agent.", AchievementTier.Bronze,
                c => c.SubAgents >= 1),
            new("middle-manager", "Middle Manager", "👔", "100 sub-agents dispatched — you manage now.", AchievementTier.Silver,
                c => c.SubAgents >= 100),
            new("puppet-master", "Puppet Master", "🎭", "1,000 sub-agents. It's agents all the way down.", AchievementTier.Gold,
                c => c.SubAgents >= 1000),
            new("team-captain", "Team Captain", "🧢", "Ran with an Agent-Teams teammate.", AchievementTier.Bronze,
                c => c.Teammates >= 1),

            // ── Breadth ──
            new("multitasker", "Multitasker", "🗂️", "Worked across 5 projects.", AchievementTier.Bronze,
                c => c.Projects >= 5),
            new("nomad", "Nomad", "🧭", "15 different projects. No fixed abode.", AchievementTier.Silver,
                c => c.Projects >= 15),
            new("branch-manager", "Branch Manager", "🌿", "10 different git branches.", AchievementTier.Bronze,
                c => c.Branches >= 10),
            new("model-citizen", "Model Citizen", "🎛️", "Used three model families across your history.", AchievementTier.Silver,
                c => c.ModelFamilies >= 3),

            // ── Tool mains ──
            new("grep-goblin", "Grep Goblin", "🔍", "500 Grep calls — nothing hides from you.", AchievementTier.Silver,
                c => c.Tool("Grep") >= 500),
            new("bash-brawler", "Bash Brawler", "💥", "500 Bash calls.", AchievementTier.Silver,
                c => c.Tool("Bash") >= 500),
            new("the-editor", "The Editor", "✏️", "1,000 Edits.", AchievementTier.Silver,
                c => c.Tool("Edit") >= 1000),
            new("well-read", "Well-Read", "📖", "1,000 Reads.", AchievementTier.Silver,
                c => c.Tool("Read") >= 1000),
            new("jack-of-all-tools", "Jack of All Tools", "🃏", "Used at least 8 different tools.", AchievementTier.Gold,
                c => c.DistinctTools >= 8),

            // ── Quirks ──
            new("cache-money", "Cache Money", "💰", "More cache reads than fresh input. Thrifty.", AchievementTier.Bronze,
                c => c.CacheRead > c.Input && c.Input > 0),
            new("one-trick-pony", "One-Trick Pony", "🐴", "One tool is 90% of your calls. We get it.", AchievementTier.Bronze,
                c => c.ToolCalls >= 100 && c.TopToolShare >= 0.9),
        };
    }

    // A badge definition: identity + the earned predicate over the precomputed Ctx. NeedsCost badges are
    // filtered out when the user hides estimated cost.
    private sealed record Def(
        string Id, string Name, string Emoji, string Description, AchievementTier Tier,
        bool NeedsCost, Func<Ctx, bool> Earned)
    {
        public Def(string id, string name, string emoji, string description, AchievementTier tier, Func<Ctx, bool> earned)
            : this(id, name, emoji, description, tier, false, earned) { }
    }

    // Everything the predicates need, computed once from the report so each badge is a cheap comparison.
    private sealed class Ctx
    {
        private readonly StatsReport _r;
        private readonly Dictionary<string, int> _tools;

        public Ctx(StatsReport r, RangeReport? range)
        {
            _r = r;
            _tools = r.Tools.ToDictionary(t => t.Tool, t => t.Count, StringComparer.Ordinal);

            int peak = -1; long max = 0;
            for (int h = 0; h < r.HourlyActiveSeconds.Length; h++)
                if (r.HourlyActiveSeconds[h] > max) { max = r.HourlyActiveSeconds[h]; peak = h; }
            PeakHour = peak;
            AllHoursActive = r.HourlyActiveSeconds.Length >= 24 && r.HourlyActiveSeconds.Take(24).All(s => s > 0);

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
        public bool AllHoursActive { get; }
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

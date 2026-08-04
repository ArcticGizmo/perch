using Perch.Data;

namespace Perch.Avalonia.Rendering;

/// <summary>
/// Synthetic overlay seed data — a representative set of <see cref="ClaudeSession"/> rows plus the usage
/// and system-metrics readings that feed the panel. Deterministic and side-effect-free: it never touches
/// the real <c>~/.claude</c>, so it's safe to render anywhere. Shared by <see cref="HeadlessRenderer"/>
/// (PNG verification) and the Settings live-preview pane, so both exercise exactly the same glyphs and a
/// new indicator only has to be added to the sample in one place.
/// </summary>
internal static class SampleData
{
    /// <summary>
    /// A cross-section of session states, chosen to light up every overlay glyph at once: a running
    /// session with a sub-agent/teammate tree, mode badge, note, context fill, burn rate, git churn and a
    /// task checklist; an awaiting-input session with a project note, an open PR and a published artifact;
    /// a needs-attention session that's stuck and remote-controlled; an idle session; an API-error row; and
    /// a background/SDK session that groups under the Autonomous section.
    /// </summary>
    public static IReadOnlyList<ClaudeSession> Sessions()
    {
        var now = DateTime.Now;
        var subs = new List<SubAgent>
        {
            // A teammate that has itself spawned a sub-agent, and a plain sub-agent nesting two levels
            // deep — the parent → sub-agent → teammate tree, exercising indent + the collapse chevron.
            new("t1", "teammate", "general-purpose", IsTeammate: true, Name: "arch-explorer",
                Color: "blue", Activity: "Reading Program.cs",
                Children: [new("t1a", "Trace the token refresh path", "Explore")]),
            new("t2", "teammate", "general-purpose", IsTeammate: true, Name: "reviewer",
                Color: "green", IsIdle: true),
            new("a1", "Explore the auth flow", "general-purpose",
                Children: [new("a1a", "Map the OAuth callback", "general-purpose",
                    Children: [new("a1b", "Read middleware config", "Explore")])]),
        };
        return
        [
            new ClaudeSession("1234", "s1", SessionStatus.Running, @"C:\src\perch", "perch", now,
                Activity: "Editing OverlayForm.cs", SubAgents: subs, Mode: PermissionMode.AcceptEdits,
                Note: "risky refactor — waiting on review",
                ContextFill: 0.82f, BurnRate: 12300, GitStats: new GitLineStats(142, 37),
                Tasks: new List<TaskItem>
                {
                    new("Extract core", "extracting core", TaskState.Completed),
                    new("Port overlay", "porting overlay", TaskState.Pending),
                    new("Cutover", "cutting over", TaskState.Pending),
                }),
            new ClaudeSession("5678", "s2", SessionStatus.AwaitingInput, @"C:\src\api", "api", now,
                ExternalNotify: true,
                // No session note, but a project note — so the row still shows the note glyph.
                ProjectNote: "API freeze — ship v0.9 before merging anything",
                PullRequest: new PullRequestInfo(1135, "https://github.com/o/r/pull/1135", "Surface PRs on the overlay", PrState.Open),
                Artifacts: new List<Artifact> { new("https://claude.ai/code/artifact/1", "API report") }),
            new ClaudeSession("9012", "s3", SessionStatus.NeedsAttention, @"C:\src\docs", "docs-site", now,
                BridgeSessionId: "bridge-xyz", Stuck: new StuckSignal(StuckKind.FailingLoop, "repeating build"),
                PullRequest: new PullRequestInfo(88, "https://github.com/o/r/pull/88", "Draft: docs restructure", PrState.Draft)),
            new ClaudeSession("3456", "s4", SessionStatus.Idle, @"C:\src\scratch", "scratch", now,
                Note: "don't touch — bisecting a flaky test",
                PullRequest: new PullRequestInfo(74, "https://github.com/o/r/pull/74", "Ship v0.9", PrState.Merged)),
            // A session whose last request to the API failed (529 Overloaded) — the red ApiError alert.
            new ClaudeSession("6543", "s6", SessionStatus.ApiError, @"C:\src\web", "web", now,
                ApiFailure: new ApiFailure(529, "API Error: 529 Overloaded.")),
            // A background/SDK session (Entrypoint != "cli") -> grouped under the Autonomous section.
            new ClaudeSession("7788", "s5", SessionStatus.Running, @"C:\src\bot", "nightly-bot", now,
                Entrypoint: "sdk-py"),
        ];
    }

    /// <summary>
    /// A healthy-but-visible reading: session bar mid-yellow, weekly bar low-green, both with a reset
    /// time an hour or two out so the expected-rate markers land partway along each track. Carries a
    /// model-scoped weekly window too, so the render exercises the variable-height three-bar strip.
    /// </summary>
    public static UsageInfo Usage()
    {
        var now = DateTime.Now;
        return new UsageInfo(
            FiveHourPercent: 62, SevenDayPercent: 28,
            FiveHourResetsAt: now.AddHours(2), SevenDayResetsAt: now.AddDays(4),
            LastUpdated: now, Ok: true, Error: null)
        {
            Scoped = [new ScopedUsage("Fable", 41, now.AddDays(4))],
        };
    }

    /// <summary>Whole-machine CPU + RAM strip reading for the metrics header.</summary>
    public static SystemMetrics SystemMetrics() =>
        new(CpuPercent: 37.5, UsedRamBytes: 12_000_000_000, TotalRamBytes: 32_000_000_000);

    /// <summary>Per-session CPU/RAM readings keyed by pid, for the two busiest sample rows.</summary>
    public static IReadOnlyDictionary<string, SessionMetrics> SessionMetrics() =>
        new Dictionary<string, SessionMetrics>
        {
            ["1234"] = new(CpuPercent: 24.0, RamBytes: 1_800_000_000, ProcessCount: 5),
            ["7788"] = new(CpuPercent: 8.0,  RamBytes: 600_000_000,   ProcessCount: 2),
        };
}

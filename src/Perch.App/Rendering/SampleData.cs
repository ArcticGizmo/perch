using Perch.Data;
using Perch.Platform;
using Perch.Social;

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
    /// a needs-attention session that's stuck, remote-controlled and has produced Markdown docs; an idle session; an API-error row; and
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
                PullRequest: new PullRequestInfo(1135, "https://github.com/o/r/pull/1135", "Surface PRs on the overlay", PrState.Open)
                {
                    Checks =
                    [
                        new("build", PrCheckState.Success),
                        new("unit-tests", PrCheckState.Failure),
                        new("lint", PrCheckState.Success),
                    ],
                },
                Artifacts: new List<Artifact> { new("https://claude.ai/code/artifact/1", "API report") }),
            new ClaudeSession("9012", "s3", SessionStatus.NeedsAttention, @"C:\src\docs", "docs-site", now,
                BridgeSessionId: "bridge-xyz", Stuck: new StuckSignal(StuckKind.FailingLoop, "repeating build"),
                HasProducedMarkdown: true,
                JiraTicket: new JiraTicketInfo("SFTY-1234", "https://acme.atlassian.net/browse/SFTY-1234"),
                PullRequest: new PullRequestInfo(88, "https://github.com/o/r/pull/88", "Draft: docs restructure", PrState.Draft)
                {
                    Checks = [new("build", PrCheckState.Pending), new("deploy-preview", PrCheckState.Pending)],
                }),
            new ClaudeSession("3456", "s4", SessionStatus.Idle, @"C:\src\scratch", "scratch", now,
                Note: "don't touch — bisecting a flaky test",
                PullRequest: new PullRequestInfo(74, "https://github.com/o/r/pull/74", "Ship v0.9", PrState.Merged)
                {
                    Checks = [new("build", PrCheckState.Success), new("e2e", PrCheckState.Success)],
                }),
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
            // Extra usage enabled with a partial monthly spend, so the (off-by-default) spend bar has
            // something to show when a preview or render turns it on.
            ExtraUsage = new ExtraUsageInfo(
                Enabled: true, Used: 24.80m, Limit: 100m, Currency: "AUD", DecimalPlaces: 2, LimitReached: false),
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

    /// <summary>Something playing, for the now-playing strip — only drawn when the media setting is on.</summary>
    public static MediaSnapshot Media() =>
        new(Title: "Weightless (Ambient Transmission, Pt. 3)", Artist: "Marconi Union",
            IsPlaying: true, CanPlayPause: true, CanNext: true, CanPrevious: false,
            Position: TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(14), Duration: TimeSpan.FromMinutes(8));

    /// <summary>An app holding the mic, for the microphone strip — only drawn when the mic setting is on.</summary>
    public static MicSnapshot Mic() =>
        new([new MicUser("91750D7E.Slack_8she8kybcnzg4", "Slack", 4242, true, DateTimeOffset.Now.AddMinutes(-7))],
            DeviceName: "Microphone (Logitech Webcam C930e)");

    /// <summary>A friends roster for the overlay's social region — me + a few friends with statuses, moods and
    /// reactions, plus one who hasn't posted, so the preview/render exercises every row shape.</summary>
    public static RosterSnapshot Roster()
    {
        var now = DateTimeOffset.UtcNow;
        var me = new Profile(Guid.Parse("dddddddd-1111-4000-8000-00000000000d"), "jon", "Jon", "😌");
        var ada = new Profile(Guid.Parse("aaaaaaaa-1111-4000-8000-000000000001"), "ada", "Ada L.", "🦉");
        var grace = new Profile(Guid.Parse("bbbbbbbb-1111-4000-8000-000000000002"), "grace", "Grace H.", "🛠️");
        var linus = new Profile(Guid.Parse("cccccccc-1111-4000-8000-000000000003"), "linus", null, "☕");

        var adaPost = new FeedItem(Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001"), ada,
            "refactored the whole thing. do not ask.", "🦉", now.AddMinutes(-2));
        var gracePost = new FeedItem(Guid.Parse("bbbbbbbb-0000-4000-8000-000000000002"), grace,
            "tests green on the first try", "🛠️", now.AddMinutes(-14));
        var myPost = new FeedItem(Guid.Parse("dddddddd-0000-4000-8000-00000000000d"), me,
            "shipping something silly", "😌", now.AddMinutes(-5));

        return new RosterSnapshot(me, myPost,
        [
            new(ada, adaPost, [new ReactionGroup("🔥", 2, true), new ReactionGroup("🎉", 1, false)]),
            new(grace, gracePost, [new ReactionGroup("👍", 1, false)]),
            new(linus, null, []),
        ], IncomingRequests: 1);
    }

    /// <summary>A couple of daemon workers, for the daemon strip — hidden when the daemon setting is off.</summary>
    public static IReadOnlyList<DaemonWorker> DaemonWorkers()
    {
        var now = DateTime.Now;
        return
        [
            new("f7d0b5fc", "f7d0b5fc-e679-492f-9fee-18a29f41602a", 61112, @"C:\src\hypertree", "hypertree",
                "slash", "Implement streamlined PowerShell install pathway", now.AddMinutes(-12)),
            new("c0ffee01", "c0ffee01-0000-4000-8000-000000000001", 70001, @"C:\src\api", "api",
                "slash", "Sweep flaky test batch 1", now.AddMinutes(-9)),
        ];
    }
}

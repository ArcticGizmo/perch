using Perch.Avalonia.Views;
using Perch.Data;
using Perch.Data.Hypertree;
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
    // Two sample config dirs so the render (and the live preview) exercise the multi-config-dir directory
    // chip (Layer 1). WorkDir is a launcher-style env; the primary stands in for ~/.claude. The renderer
    // installs a two-dir set (SetForTesting) so ClaudeConfigSet.IsMulti is true and the chip draws on the
    // sessions tagged with WorkDir below.
    internal static readonly ClaudeConfigDir WorkDir =
        new(@"C:\Users\me\.claude-envs\envs\work", slug: "work")
        { Provenance = ConfigDirProvenance.Declared };
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
                ProjectNote: "risky refactor — waiting on review",
                ContextFill: 0.82f, BurnRate: 12300, GitStats: new GitLineStats(142, 37),
                Tasks: new List<TaskItem>
                {
                    new("Extract core", "extracting core", TaskState.Completed),
                    new("Port overlay", "porting overlay", TaskState.Pending),
                    new("Cutover", "cutting over", TaskState.Pending),
                })
            {
                // Attributed to a non-primary config dir, so the multi-dir "work" chip renders on this row.
                ConfigDir = WorkDir, ReportedConfigDir = WorkDir.Root,
            },
            new ClaudeSession("5678", "s2", SessionStatus.AwaitingInput, @"C:\src\api", "api", now,
                ExternalNotify: true,
                // A project note — the row shows the amber note glyph.
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
                ProjectNote: "don't touch — bisecting a flaky test",
                PullRequest: new PullRequestInfo(74, "https://github.com/o/r/pull/74", "Ship v0.9", PrState.Merged)
                {
                    Checks = [new("build", PrCheckState.Success), new("e2e", PrCheckState.Success)],
                })
            {
                // Idle, but a dev server and a log tail are still running: the count is what says so.
                BackgroundTasks =
                [
                    new("bdev1", BackgroundTaskKind.Shell, DateTime.UtcNow.AddMinutes(-42), null, "Start the dev server"),
                    new("blog1", BackgroundTaskKind.Shell, DateTime.UtcNow.AddMinutes(-40), null, "tail -f logs/app.log"),
                ],
            },
            // A session whose last request to the API failed (529 Overloaded) — the red ApiError alert.
            new ClaudeSession("6543", "s6", SessionStatus.ApiError, @"C:\src\web", "web", now,
                ApiFailure: new ApiFailure(529, "API Error: 529 Overloaded.")),
            // A Claude Desktop session (Entrypoint == "claude-desktop") -> interactive, listed with the
            // rest but marked with the monitor glyph.
            new ClaudeSession("5566", "s7", SessionStatus.Running, @"C:\src\thoughts", "claude-thoughts", now,
                Entrypoint: "claude-desktop"),
            // A background/SDK session (Entrypoint != "cli") -> grouped under the Autonomous section.
            new ClaudeSession("7788", "s5", SessionStatus.Running, @"C:\src\bot", "nightly-bot", now,
                Entrypoint: "sdk-py"),
            // IDE-hosted sessions — each marked with its host editor's origin glyph. A coloured brand
            // (VS Code), a monochrome one (Cursor), and a JetBrains IDE; the Windsurf sail and the generic
            // "</>" fallback are exercised by the unit tests / render captures.
            new ClaudeSession("8801", "s8", SessionStatus.Running, @"C:\src\ext", "extension", now,
                IdeHost: new IdeHost(IdeHostKind.VsCode, "Visual Studio Code", "code"))
            {
                // A Monitor watching the build while it works: the row's "❯ 1" background count.
                BackgroundTasks = [new("bmon1", BackgroundTaskKind.Monitor, DateTime.UtcNow.AddMinutes(-6), null, "build errors")],
            },
            new ClaudeSession("8802", "s9", SessionStatus.Idle, @"C:\src\agent", "agent", now,
                IdeHost: new IdeHost(IdeHostKind.Cursor, "Cursor", "cursor"))
            {
                ConfigDir = WorkDir, ReportedConfigDir = WorkDir.Root,
            },
            new ClaudeSession("8803", "s10", SessionStatus.AwaitingInput, @"C:\src\svc", "service", now,
                IdeHost: new IdeHost(IdeHostKind.JetBrains, "PyCharm", "pycharm64")),
            // A Perch session with no process (Perch closed while it was open): faded, "not running", plan mode kept.
            new ClaudeSession("~s11", "s11", SessionStatus.Idle, @"C:\src\billing", "billing", now.AddHours(-3),
                Mode: PermissionMode.Plan, Title: "Invoice export", PerchControlled: true) { IsDormant = true },
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

    /// <summary>Two orgs' worth of readings for the per-org usage strip — exercises the org headings, a
    /// second set, the condensed labels and the credits bar. The dirs are synthetic; the resolved orgs give
    /// the headings real names.</summary>
    public static IReadOnlyList<OrgUsage> OrgUsages()
    {
        var now = DateTime.Now;
        var acme = new UsageInfo(
            FiveHourPercent: 62, SevenDayPercent: 28,
            FiveHourResetsAt: now.AddHours(2), SevenDayResetsAt: now.AddDays(4),
            LastUpdated: now, Ok: true, Error: null)
        {
            Scoped = [new ScopedUsage("Fable", 41, now.AddDays(4))],
            ExtraUsage = new ExtraUsageInfo(true, 24.80m, 100m, "AUD", 2, false),
        };
        var beta = new UsageInfo(
            FiveHourPercent: 88, SevenDayPercent: 63,
            FiveHourResetsAt: now.AddHours(1), SevenDayResetsAt: now.AddDays(2),
            LastUpdated: now, Ok: true, Error: null)
        {
            Scoped = [new ScopedUsage("Fable", 72, now.AddDays(2))],
        };
        return
        [
            new OrgUsage(new ClaudeConfigDir(@"C:\Users\sample\.claude"), new Org("org-a") { Name = "Acme Corp" }, acme),
            new OrgUsage(new ClaudeConfigDir(@"C:\envs\beta\.claude", slug: "beta"), new Org("org-b") { Name = "Beta Inc" }, beta),
        ];
    }

    /// <summary>A larger estate (seven orgs) with a real spread of healthy / watch / near-limit readings, for
    /// eyeballing the multi-org strip: the first two are Active (full bars), the rest are known-but-idle
    /// (compact chips by default) — the mixed view the overlay shows at startup. Exercises the chip wrap, the
    /// label-free mini-bars, the pace ticks, and the fresh-window "safe zone" (the "redux" account: barely any
    /// time elapsed and barely any usage → green, not yellow).</summary>
    public static IReadOnlyList<OrgUsage> OrgUsagesMany()
    {
        var now = DateTime.Now;
        UsageInfo U(int h5, int wk, int fab, decimal? spend, decimal cap, double h5In, double wkIn) => new(
            FiveHourPercent: h5, SevenDayPercent: wk,
            FiveHourResetsAt: now.AddHours(5 - h5In), SevenDayResetsAt: now.AddDays(7 - wkIn),
            LastUpdated: now, Ok: true, Error: null)
        {
            Scoped = [new ScopedUsage("Fable", fab, now.AddDays(3))],
            ExtraUsage = spend is { } s ? new ExtraUsageInfo(true, s, cap, "AUD", 2, false) : null,
        };
        (string name, UsageInfo u)[] orgs =
        [
            ("redux",     U(0,  0,  0,  null, 0m,  0.4, 0.3)),   // fresh window, ~no usage → safe-zone green
            ("initrode",  U(62, 34, 45, 12m, 80m,  3.5, 2.8)),
            ("cyberdyne", U(18, 51, 22, null, 50m, 1.5, 4.2)),
            ("acme",      U(88, 72, 61, 44m, 60m,  2.7, 4.6)),
            ("globex",    U(41, 29, 30, null, 40m, 3.0, 3.5)),
            ("initech",   U(74, 91, 83, 70m, 75m,  2.5, 5.6)),
            ("umbrella",  U(9,  14, 5,  null, 30m, 2.0, 2.1)),
        ];
        return orgs
            .Select((o, i) => new OrgUsage(
                new ClaudeConfigDir($@"C:\envs\{o.name}\.claude", slug: o.name),
                new Org($"org-{i}") { Name = o.name }, o.u, Active: i < 2))   // first two active, rest idle
            .ToList();
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

        // Reactions friends left on your own status — shown on the "you" row. Handles feed the hover tooltip.
        IReadOnlyList<ReactionGroup> myReactions =
            [new ReactionGroup("🎉", 2, false, ["ada", "grace"]), new ReactionGroup("😂", 1, false, ["linus"])];

        return new RosterSnapshot(me, myPost, myReactions,
        [
            // ada: >2 distinct emojis → collapses to a combined count chip (tooltip shows who reacted, per emoji).
            new(ada, adaPost, [
                new ReactionGroup("🔥", 3, true, ["you", "grace", "linus"]),
                new ReactionGroup("❤️", 2, false, ["grace", "linus"]),
                new ReactionGroup("👍", 1, false, ["grace"])]),
            // grace: two distinct → shown as individual chips.
            new(grace, gracePost, [
                new ReactionGroup("👍", 2, false, ["ada", "linus"]),
                new ReactionGroup("🎉", 1, false, ["ada"])]),
            new(linus, null, []),
        ], IncomingRequests: 1);
    }

    /// <summary>A Hypertree stack with more than one branch, so the strip shows in the preview/render (a lone
    /// "main" line is suppressed). Cursor sits on the first branch.</summary>
    public static HypertreeStatus Hypertree()
    {
        HypertreeRow Row(string kind, string name, string desktop) => new()
        {
            Kind = kind, Name = name, Cursor = 0,
            Id = kind == "main" ? null : Guid.NewGuid(),
            Desktops = [new HypertreeDesktop { Label = desktop }],
        };

        return new HypertreeStatus
        {
            Schema = 1, Version = "1.0", Pid = 4242,
            Rows =
            [
                Row("main", "main", "Desktop 1"),
                Row("branch", "overlay-port", "Desktop 2"),
                Row("branch", "social-feed", "Desktop 3"),
            ],
            Current = new HypertreePosition { Row = 1, Desktop = 0 },
        };
    }

    /// <summary>A few outstanding to-dos for the Todo section — one overdue, so the preview exercises both the
    /// normal and attention hues. Returned already sorted + capped, the way the monitor host feeds them.</summary>
    public static IReadOnlyList<OverlayCanvas.TodoLine> Todos() =>
    [
        new("t-1", "Review the overlay layout PR", "in 2h", Overdue: false),
        new("t-2", "Reply to the design thread", "yesterday", Overdue: true),
        new("t-3", "Cut the next release", "Fri", Overdue: false),
    ];

    /// <summary>The overlay feeds row: two feeds with news (one in double figures), two read, one failing — icons
    /// left out so the initials fallback shows (the app decodes real ones off the UI thread).</summary>
    public static IReadOnlyList<Views.OverlayCanvas.FeedHeadView> FeedHeads()
    {
        var now = DateTime.UtcNow;
        return
        [
            new("rt", "Release notes from runtime", null, 2, ".NET 10.0.3", now.AddHours(-2), null, "https://github.com/dotnet/runtime/releases"),
            new("av", "Avalonia blog", null, 12, "Avalonia 12 preview 3", now.AddHours(-5), null, "https://avaloniaui.net/blog"),
            new("hn", "Hacker News: Show HN", null, 0, "Show HN: A tiny Atom reader", now.AddDays(-1), null, null),
            new("gh", "GitHub Changelog", null, 0, "Copilot code review updates", now.AddDays(-2), null, "https://github.blog/changelog"),
            new("st", "Status page", null, 0, null, null, "404 Not Found", null),
        ];
    }

    /// <summary>A parsed feed for the feed dialog / row / story renders: a releases feed with a few entries.</summary>
    public static Perch.Feeds.FeedDoc FeedDoc()
    {
        var now = DateTime.UtcNow;
        Perch.Feeds.FeedEntry E(string id, string title, double hoursAgo) =>
            new(id, title, $"https://github.com/dotnet/runtime/releases/tag/{id}", "dotnet-bot", null,
                now.AddHours(-hoursAgo), $"<p>Release notes for <strong>{title}</strong>.</p>", null, $"Release notes for {title}.");
        return new Perch.Feeds.FeedDoc("Release notes from runtime", "https://github.com/dotnet/runtime/releases", null, now,
            [E("v10.0.3", ".NET 10.0.3", 2), E("v10.0.2", ".NET 10.0.2", 26), E("v10.0.1", ".NET 10.0.1", 300)]);
    }

    /// <summary>The story player's feeds: runtime releases (2 unread), the Avalonia blog (3 unread, one a long post
    /// with every kind of block), Show HN (all read) and a failing status page — in the engine's display order.</summary>
    public static Perch.Feeds.FeedsSnapshot FeedStory()
    {
        var now = DateTime.UtcNow;
        var rt = FeedDoc();
        Perch.Feeds.FeedEntry E(string id, string title, string author, double hoursAgo, string html) =>
            new(id, title, $"https://avaloniaui.net/blog/{id}", author, now.AddHours(-hoursAgo), now.AddHours(-hoursAgo),
                html, "https://avaloniaui.net/blog/", null);
        var av = new List<Perch.Feeds.FeedEntry>
        {
            E("p3", "Avalonia 12 preview 3", "Dan Walmsley", 5,
                "<p>Preview 3 is out, with the new <strong>text layout</strong> engine on by default.</p>" +
                "<p>Try it and tell us what breaks.</p>"),
            E("p2", "Inside the compositor: how a frame gets to the screen", "Nikita Tsukanov", 30, LongPost()),
            E("p1", "Avalonia 12 preview 2", "Dan Walmsley", 200, "<p>Preview 2 brings the <em>headless</em> platform up to date.</p>"),
        };
        var hn = new List<Perch.Feeds.FeedEntry>
        {
            new("hn2", "Show HN: A tiny Atom reader", "https://news.ycombinator.com/item?id=2", "pg", null, now.AddDays(-1),
                "<p>Built it in a weekend. No JavaScript, no tracking.</p>", null, null),
            new("hn1", "Show HN: Story rings for RSS", "https://news.ycombinator.com/item?id=1", "dang", null, now.AddDays(-3),
                null, null, "A feed reader that plays posts like stories."),
        };

        Perch.Feeds.FeedHead H(string id, string title, string? site, IReadOnlyList<Perch.Feeds.FeedEntry> entries, int unread, string? error = null) =>
            new(id, title, site, null, unread, unread > 0 ? entries[0].Updated : null, entries.FirstOrDefault()?.Title,
                entries.FirstOrDefault()?.Updated, error, entries.Count > 0, false);
        Perch.Feeds.StoryFeed S(string id, IReadOnlyList<Perch.Feeds.FeedEntry> entries, int unread, string? error = null) =>
            new(id, entries, entries.Take(unread).Select(e => e.Id).ToHashSet(StringComparer.Ordinal), error);

        return new Perch.Feeds.FeedsSnapshot(
            [
                H("rt", rt.Title, rt.SiteUrl, rt.Entries, 2),
                H("av", "Avalonia blog", "https://avaloniaui.net/blog", av, 2),
                H("hn", "Hacker News: Show HN", "https://news.ycombinator.com/show", hn, 0),
                H("st", "Status page", null, [], 0, "404 Not Found"),
            ],
            [S("rt", rt.Entries, 2), S("av", av, 2), S("hn", hn, 0), S("st", [], 0, "404 Not Found")]);

        static string LongPost() =>
            "<p>Every frame Avalonia draws goes through the <a href=\"/docs/compositor\">compositor</a>. " +
            "This post follows one from a property change to the swap chain.</p>" +
            "<h2>1. Invalidation</h2><p>A control calls <code>InvalidateVisual()</code>. Nothing is drawn yet; the " +
            "visual is only <em>marked</em> dirty.</p>" +
            "<ul><li>Layout runs first, if anything was measured.</li><li>Then render, once per frame at most.</li>" +
            "<li>Animations tick on the same clock.</li></ul>" +
            "<h2>2. Recording</h2><p>The UI thread records draw calls into a list, cheaply:</p>" +
            "<pre><code>public override void Render(DrawingContext ctx)\n{\n    ctx.DrawRectangle(Brushes.Red, null, Bounds);\n}</code></pre>" +
            "<blockquote><p>The UI thread never touches the GPU. That's the whole point.</p></blockquote>" +
            "<h2>3. Composition</h2><p>The render thread replays the lists into the scene graph.</p>" +
            "<table><tr><th>Stage</th><th>Thread</th></tr><tr><td>Record</td><td>UI</td></tr>" +
            "<tr><td>Compose</td><td>Render</td></tr><tr><td>Present</td><td>Render</td></tr></table>" +
            "<p><img src=\"/img/frame.png\" alt=\"A frame's journey\"></p>" +
            string.Concat(Enumerable.Range(1, 6).Select(i =>
                $"<p>Paragraph {i}: the dirty rects are merged, clipped to the window and handed to Skia, which " +
                "rasterises them into the back buffer before the swap.</p>")) +
            "<p>Questions? Find us on <a href=\"https://github.com/AvaloniaUI/Avalonia/discussions\">GitHub Discussions</a>.</p>";
    }

    /// <summary>xkcd, parsed from an Atom document shaped like xkcd.com/atom.xml (the comic is a summary
    /// <c>&lt;img&gt;</c> whose <c>title</c> is the hover text), with its images switch as given.</summary>
    public static Perch.Feeds.FeedsSnapshot FeedStoryXkcd(bool showImages)
    {
        const string xml =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?><feed xmlns=\"http://www.w3.org/2005/Atom\" xml:lang=\"en\">" +
            "<title>xkcd.com</title><link href=\"https://xkcd.com/\" rel=\"alternate\"/><id>https://xkcd.com/</id>" +
            "<updated>2026-10-06T00:00:00Z</updated>" +
            "<entry><title>Feed Reader</title><link href=\"https://xkcd.com/3001/\" rel=\"alternate\"/>" +
            "<updated>2026-10-06T00:00:00Z</updated><id>https://xkcd.com/3001/</id>" +
            "<summary type=\"html\">&lt;img src=\"https://imgs.xkcd.com/comics/feed_reader.png\" " +
            "title=\"I added a ring that lights up when there's news. Now I check the ring instead of the news.\" " +
            "alt=\"Feed Reader\" /&gt;</summary></entry></feed>";
        var doc = Perch.Feeds.FeedParser.Parse(xml, new Uri("https://xkcd.com/atom.xml"), DateTime.UtcNow).Doc!;
        var head = new Perch.Feeds.FeedHead("xk", doc.Title, doc.SiteUrl, null, 1, doc.Entries[0].Updated,
            doc.Entries[0].Title, doc.Entries[0].Updated, null, true, false, ShowImages: showImages);
        var story = new Perch.Feeds.StoryFeed("xk", doc.Entries, doc.Entries.Select(e => e.Id).ToHashSet(StringComparer.Ordinal), null);
        return new Perch.Feeds.FeedsSnapshot([head], [story]);
    }

    /// <summary>One feed built from a hostile Atom document through the real parser, so the render shows what an
    /// attack actually turns into: script and styles gone, dangerous links as plain text, Markdown syntax literal,
    /// a disguised link showing its real host, and path-like links inert.</summary>
    public static Perch.Feeds.FeedsSnapshot FeedStoryHostile()
    {
        const string content =
            "&lt;p&gt;Hello &lt;script&gt;alert(1)&lt;/script&gt;&lt;style&gt;body{display:none}&lt;/style&gt;" +
            "&lt;b onclick=&quot;x()&quot;&gt;world&lt;/b&gt;.&lt;/p&gt;" +
            "&lt;p&gt;[click me](javascript:alert(1)) ![](http://track.example/p.gif) # not a heading | a | b |&lt;/p&gt;" +
            "&lt;p&gt;&lt;a href=&quot;jav&amp;#x61;script:alert(1)&quot;&gt;encoded javascript&lt;/a&gt;, " +
            "&lt;a href=&quot;file:///C:/Windows/win.ini&quot;&gt;file link&lt;/a&gt;, " +
            "&lt;a href=&quot;C:\\Windows\\System32\\calc.exe&quot;&gt;drive path&lt;/a&gt;, " +
            "&lt;a href=&quot;\\\\host\\share\\x.exe&quot;&gt;UNC path&lt;/a&gt;, " +
            "&lt;a href=&quot;ms-settings:privacy&quot;&gt;ms-settings&lt;/a&gt;.&lt;/p&gt;" +
            "&lt;p&gt;&lt;a href=&quot;https://evil.example/login&quot;&gt;https://paypal.com/login&lt;/a&gt;&lt;/p&gt;" +
            "&lt;p&gt;Inline path: &lt;code&gt;C:\\Windows\\System32\\drivers\\etc\\hosts&lt;/code&gt;&lt;/p&gt;" +
            "&lt;iframe src=&quot;https://evil.example&quot;&gt;framed&lt;/iframe&gt;&lt;svg onload=&quot;x()&quot;&gt;&lt;text&gt;svg&lt;/text&gt;&lt;/svg&gt;";
        string xml =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?><feed xmlns=\"http://www.w3.org/2005/Atom\">" +
            "<title>Totally Safe Blog</title><link rel=\"alternate\" href=\"https://evil.example/\"/>" +
            "<entry><id>urn:x:1</id><updated>2026-10-07T09:00:00Z</updated>" +
            "<title type=\"html\">&lt;b&gt;Perch&lt;/b&gt; update\u202Egnp.exe\u202C\nrequired: click here</title>" +
            "<author><name>Perch\u200B Team</name></author>" +
            "<link href=\"javascript:alert(1)\"/>" +
            "<content type=\"html\">" + content + "</content></entry></feed>";
        var doc = Perch.Feeds.FeedParser.Parse(xml, new Uri("https://evil.example/feed.atom"), DateTime.UtcNow).Doc;
        var entries = doc?.Entries ?? [];
        var head = new Perch.Feeds.FeedHead("ev", doc?.Title ?? "Hostile", doc?.SiteUrl, null, entries.Count,
            entries.FirstOrDefault()?.Updated, entries.FirstOrDefault()?.Title, entries.FirstOrDefault()?.Updated, null, true, false);
        var story = new Perch.Feeds.StoryFeed("ev", entries, entries.Select(e => e.Id).ToHashSet(StringComparer.Ordinal), null);
        return new Perch.Feeds.FeedsSnapshot([head], [story]);
    }

    /// <summary>A GitHub alerts poll covering every reason the classifier can raise across three repos, plus PRs that
    /// need nothing — so the strip, the window's "Needs you" view and its "All open" view all have something to show.
    /// The viewer is "me"; times are relative to now so the "updated …" labels read naturally.</summary>
    public static GitHubFetchResult GitHubAlerts()
    {
        var now = DateTime.UtcNow;
        GhEvent Ev(string who, GhEventKind k, int minutesAgo, bool bot = false) => new(who, bot, k, now.AddMinutes(-minutesAgo));
        GhPullRequest Pr(string repo, int n, string title, string author, GhPrRelation rel, int updatedMins, string head) => new()
        {
            Repo = repo, Number = n, Title = title, Url = $"https://github.com/{repo}/pull/{n}", Author = author,
            Relation = rel, UpdatedUtc = now.AddMinutes(-updatedMins), LastCommitUtc = now.AddHours(-6),
            Mergeable = GhMergeable.Mergeable, MergeState = "CLEAN", Checks = GhChecks.Passing,
            ReviewDecision = "REVIEW_REQUIRED", HeadBranch = head, BaseBranch = "main", HeadRepo = repo,
        };
        return new GitHubFetchResult("me",
        [
            Pr("acme/web", 412, "Move the checkout form to the new design system", "me", GhPrRelation.Author, 25, "feature/checkout-form") with
                { Events = [Ev("alice", GhEventKind.ChangesRequested, 25)], ReviewDecision = "CHANGES_REQUESTED" },
            Pr("acme/web", 418, "Lazy-load the dashboard charts", "bob", GhPrRelation.ReviewRequested, 90, "perf/lazy-charts"),
            Pr("acme/web", 401, "Bump the build toolchain", "me", GhPrRelation.Author, 60 * 26, "chore/toolchain") with
                { ReviewDecision = "APPROVED", HasApproval = true, Events = [Ev("carol", GhEventKind.Approved, 60 * 26)] },
            Pr("acme/api", 77, "Rate-limit the export endpoint", "me", GhPrRelation.Author, 12, "feat/export-rate-limit") with
                { Checks = GhChecks.Failing, Events = [Ev("dave", GhEventKind.Comment, 12), Ev("erin", GhEventKind.Commented, 8)] },
            Pr("acme/api", 80, "Fix the pagination off-by-one", "frank", GhPrRelation.Assignee, 240, "fix/pagination"),
            Pr("acme/api", 69, "Drop the legacy auth shim", "me", GhPrRelation.Author, 60 * 50, "chore/drop-auth-shim") with
                { Mergeable = GhMergeable.Conflicting, MergeState = "DIRTY" },
            Pr("tools/cli", 5, "Add a --json flag to status", "me", GhPrRelation.Author, 60 * 3, "feat/status-json") with
                { IsDraft = true, Checks = GhChecks.Pending, Events = [Ev("ci-bot", GhEventKind.Comment, 30, bot: true)] },
        ], null, now.AddMinutes(-2));
    }

    /// <summary>The overlay strip for <see cref="GitHubAlerts"/>, folded the way the monitor host folds it.</summary>
    public static OverlayCanvas.GitHubStrip GitHubStrip() =>
        Services.GitHubAlertsMonitorHost.ToStrip(
            GitHubAlertsClassifier.Build(GitHubAlerts(), new Dictionary<string, DateTime>()));

    /// <summary>The Recent button's lines (session recovery): one Perch had open, one a restart interrupted (so also
    /// before the shutdown), one that ended just before the shutdown, ordinary endings and an <c>/exit</c> — every tone
    /// and every filter.</summary>
    public static IReadOnlyList<Perch.Data.RecentLine> RecentLines() =>
    [
        new("r-1", @"C:\src\billing", "Invoice export", "billing", "was open", Perch.Data.RecentTone.Perch, true, false),
        new("r-2", @"C:\src\gateway", "Retry storm fix", "gateway", "interrupted · 14h", Perch.Data.RecentTone.Flagged, true, true),
        new("r-3", @"C:\src\notes", "notes", null, "before shutdown · 14h", Perch.Data.RecentTone.Flagged, false, true),
        new("r-4", @"C:\src\docs-site", "docs-site", null, "2h ago", Perch.Data.RecentTone.Normal, false, false),
        new("r-5", @"C:\src\perch", "Overlay recent button with a long title", "perch", "5h ago", Perch.Data.RecentTone.Normal, false, false),
        new("r-6", @"C:\src\scratch", "scratch", null, "1d ago", Perch.Data.RecentTone.Faded, false, false),
    ];

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

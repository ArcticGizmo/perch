using System.Diagnostics;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using Perch.Avalonia.Services;
using Perch.Avalonia.Theming;
using Perch.Data;
using Perch.Data.Control;
using Perch.Data.Roost;

namespace Perch.Avalonia.Rendering;

/// <summary>
/// <c>perch bench-roost [turns]</c>: times the Roost's Tiled interactions headlessly over six panes with long
/// conversations — a drag's ghost moves, a focus change, a drop that swaps two cells, a drop from the rail (a
/// pane's first appearance, and one brought back), and a refresh. Per operation it prints the time to the first
/// frame (the call, the layout it needs and one render, with background work left queued) and the time until
/// background work (older turns filling in) is done.
/// </summary>
internal static class RoostBench
{
    public static int Run(int turns)
    {
        AppSettings.DisablePersistence();
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .WithInterFont()
            .SetupWithoutStarting();

        var now = Clock.Now;
        var sessions = Enumerable.Range(1, 12).Select(i => new ClaudeSession(
            $"{1000 + i}", $"bench-{i}", SessionStatus.Running, $@"C:\src\proj{i}", $"proj{i}", now,
            RunningSince: now.AddMinutes(-i))).ToList();
        var roster = new RoostRoster();
        roster.Update(sessions.Take(6).ToList(), now);
        // Two 3×2 tabs: the first six sessions in A, B empty until the rest arrive.
        var tabs = new RoostTabSet();
        tabs.Sync(roster.Panes);
        var a = tabs.CreateDefault(roster.Panes, 1.6)!;
        var b = tabs.AddTab("B", RoostGridLayout.FromTemplate(RoostSnapTemplate.Grid3x2))!;

        var w = new Windows.RoostWindow(roster, tabs,
            pane => RoostFeed.ForFixed(LongConversation(turns), pane.Session.SessionId),
            SessionPalette.For(true))
        { Width = 1600, Height = 1000 };
        w.Show();
        Settle();

        Console.WriteLine($"6 panes per tab, {turns} turns each");
        Time("ghost move (drag hover)", 60, i => w.DragForRender("1001", i % 6), after: () => w.DropForRender());
        Time("focus change only", 20, i => w.FocusPane(i % 2 == 0 ? "1001" : "1002"));
        Time("drop: swap two regions", 20, i => w.PlacePane("1001", i % 2 == 0 ? 1 : 0));
        roster.Update(sessions, now);
        tabs.Sync(roster.Panes);
        w.RosterChanged();
        Settle();
        Time("drop: from rail, first time", 6, i => w.PlacePane($"{1007 + i}", i % 6));
        Time("drop: from rail, again", 10, i => w.PlacePane(i % 2 == 0 ? "1007" : "1008", 2));
        // Fill B, then flip between the tabs: every pane stays warm, so a switch is a show, not a rebuild.
        w.ActivateTab(b.Id);
        for (int i = 0; i < 6; i++) w.PlacePane($"{1007 + i}", i);
        Settle();
        Time("tab switch (warm panes)", 20, i => w.ActivateTab(i % 2 == 0 ? a.Id : b.Id));
        Time("refresh (a scan landing)", 20, _ => w.RosterChanged());
        w.Close();
        return 0;
    }

    private static void Time(string label, int n, Action<int> op, Action? after = null)
    {
        var first = new List<double>(n);
        var settled = new List<double>(n);
        for (int i = 0; i < n; i++)
        {
            var sw = Stopwatch.StartNew();
            op(i);
            Frame();
            first.Add(sw.Elapsed.TotalMilliseconds);
            Settle();
            settled.Add(sw.Elapsed.TotalMilliseconds);
        }
        after?.Invoke();
        Settle();
        first.Sort();
        settled.Sort();
        Console.WriteLine($"{label,-30} first frame median {first[n / 2],7:F1} ms  max {first[^1],7:F1}"
                          + $"   settled median {settled[n / 2],7:F1} ms   (n={n})");
    }

    // One frame: jobs at Loaded priority and above (layout, input, render), then a render tick. Background
    // jobs stay queued, as they would behind a real frame.
    private static void Frame()
    {
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Loaded);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Loaded);
    }

    // Everything queued, background included, then a frame.
    private static void Settle()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    // A long, realistic-shaped thread: prompt, a read, an edit, a build, and a markdown answer per turn.
    private static SessionConversation LongConversation(int turns)
    {
        var c = new SessionConversation();
        for (int t = 0; t < turns; t++)
        {
            c.AddUserPrompt($"Turn {t}: tighten the retry policy in the worker and add a test for the backoff.");
            c.Apply(new ToolUseEvent($"r{t}", "Read", "Reading Worker.cs", "{\"file_path\":\"src/Worker.cs\"}"));
            c.Apply(new ToolResultEvent($"r{t}", string.Join("\n", Enumerable.Range(0, 40).Select(l => $"line {l}")), false));
            c.Apply(new ToolUseEvent($"e{t}", "Edit", "Editing Worker.cs",
                "{\"file_path\":\"src/Worker.cs\",\"old_string\":\"retries = 3\",\"new_string\":\"retries = 5\"}"));
            c.Apply(new ToolResultEvent($"e{t}", "The file has been updated.", false));
            c.Apply(new ToolUseEvent($"b{t}", "Bash", "Running dotnet test", "{\"command\":\"dotnet test\"}"));
            c.Apply(new ToolResultEvent($"b{t}", "Passed!  - Failed: 0, Passed: 412", false));
            c.Apply(new AssistantTextEvent(
                "Done. The worker now retries **five** times with exponential backoff.\n\n" +
                "- `BackoffTests.DoublesEachAttempt` covers the delays\n- the cap stays at 30s\n\n" +
                "```csharp\nvar delay = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempt)));\n```"));
            c.Apply(new TurnResultEvent(false, "success", 0.12, 1200, 800, 42_000));
        }
        return c;
    }
}

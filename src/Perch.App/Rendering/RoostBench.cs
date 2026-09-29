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
/// conversations — a drag's ghost moves, a drop that swaps two cells, and a drop from the rail — and prints
/// milliseconds per operation (each a layout pass plus a rendered frame).
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
        var sessions = Enumerable.Range(1, 7).Select(i => new ClaudeSession(
            $"{1000 + i}", $"bench-{i}", SessionStatus.Running, $@"C:\src\proj{i}", $"proj{i}", now,
            RunningSince: now.AddMinutes(-i))).ToList();
        var roster = new RoostRoster();
        roster.Update(sessions.Take(6).ToList(), now);

        var w = new Windows.RoostWindow(roster,
            pane => RoostFeed.ForFixed(LongConversation(turns), pane.Session.SessionId),
            SessionPalette.For(true))
        { Width = 1600, Height = 1000 };
        w.Show();
        Pump();

        Console.WriteLine($"6 panes on stage, {turns} turns each");
        Time("ghost move (drag hover)", 60, i => w.DragForRender("1001", i % 6), after: () => w.DropForRender());
        Time("focus change only", 20, i => w.FocusPane(i % 2 == 0 ? "1001" : "1002"));
        Time("drop: swap two cells", 20, i => w.PlacePane("1001", i % 2 == 0 ? 1 : 0));
        roster.Update(sessions, now);
        w.RosterChanged();
        Pump();
        Time("drop: from the rail", 10, i => w.PlacePane(i % 2 == 0 ? "1007" : "1003", 2));
        Time("refresh (a scan landing)", 20, _ => w.RosterChanged());
        w.Close();
        return 0;
    }

    private static void Time(string label, int n, Action<int> op, Action? after = null)
    {
        var times = new List<double>(n);
        var ops = new List<double>(n);
        var layouts = new List<double>(n);
        for (int i = 0; i < n; i++)
        {
            var sw = Stopwatch.StartNew();
            op(i);
            double opMs = sw.Elapsed.TotalMilliseconds;
            Dispatcher.UIThread.RunJobs();
            double layoutMs = sw.Elapsed.TotalMilliseconds - opMs;
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            times.Add(sw.Elapsed.TotalMilliseconds);
            ops.Add(opMs);
            layouts.Add(layoutMs);
        }
        after?.Invoke();
        Pump();
        times.Sort();
        ops.Sort();
        layouts.Sort();
        Console.WriteLine($"{label,-28} median {times[n / 2],7:F1} ms   max {times[^1],7:F1} ms   "
                          + $"(call {ops[n / 2]:F1}, layout {layouts[n / 2]:F1}, render {times[n / 2] - ops[n / 2] - layouts[n / 2]:F1}; n={n})");
    }

    private static void Pump()
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

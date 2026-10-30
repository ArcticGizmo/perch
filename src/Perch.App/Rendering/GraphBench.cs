using System.Diagnostics;
using Perch.Data;
using Perch.Data.GitGraph;

namespace Perch.Avalonia.Rendering;

/// <summary>
/// <c>perch bench-graph [repo] [count]</c>: times the git graph's read and layout on a real repository, so the
/// budgets in <c>docs/git-graph-plan.md</c> can be checked against big repos. It prints what opening the window
/// costs (each stage of the first page), a refresh with lane hints, the full history up to
/// <c>count</c> commits split into git / parse / layout with the memory it holds, and the cost of paging deep
/// with <c>--skip</c>. Read-only: it runs the same Automatic-trust git reads as the window.
/// </summary>
internal static class GraphBench
{
    public static int Run(string path, int count)
    {
        if (GitCheckoutScanner.FindRoot(Path.GetFullPath(path)) is not { } root)
        {
            Console.Error.WriteLine($"Not in a git repository: {path}");
            return 1;
        }
        Console.WriteLine($"bench-graph {root}");

        Console.WriteLine($"\nOpen (All scope, first page of {GraphLoader.PageSize}):");
        var sw = Stopwatch.StartNew();
        var open = GraphLoader.Load(root, GraphScope.All, timing: Stage);
        Line("total", sw.Elapsed);
        if (open is null)
            return 1;
        Console.WriteLine($"  {open.Commits.Count} rows, {open.Width} lanes, {open.Worktrees.Count} worktrees " +
                          $"({open.Tethers.Count} dirty), {open.Refs.Count} refs");

        Console.WriteLine("\nRefresh (same scope, lane hints from the open):");
        sw.Restart();
        var refresh = GraphLoader.Load(root, GraphScope.All, open, Stage);
        Line("total", sw.Elapsed);
        if (refresh is not null)
        {
            int moved = 0;
            for (int i = 0; i < open.Commits.Count; i++)
                if (refresh.RowOf(open.Commits[i].Hash) is var r and >= 0 && refresh.Rows[r].Lane != open.Rows[i].Lane)
                    moved++;
            Console.WriteLine($"  {moved} commits changed lane");
        }

        Console.WriteLine($"\nFull history (up to {count:N0} commits):");
        GC.Collect();
        long before = GC.GetTotalMemory(forceFullCollection: true);
        sw.Restart();
        var raw = GraphLogReader.ReadLog(root, count);
        Line("git log", sw.Elapsed);
        if (raw is null)
        {
            Console.WriteLine("  git log failed or timed out");
            return 1;
        }
        Console.WriteLine($"  {raw.Length / 1024.0 / 1024.0:F1} MB of log output");
        sw.Restart();
        var log = GraphLogReader.ParseLog(raw);
        Line("parse", sw.Elapsed);
        raw = null;
        sw.Restart();
        var lanes = new LaneAllocator(new LaneAllocator.Options(PrimaryTip(open)));
        lanes.Add(log);
        Line("layout", sw.Elapsed);
        long after = GC.GetTotalMemory(forceFullCollection: true);
        Console.WriteLine($"  {log.Count:N0} commits, {lanes.Width} lanes, " +
                          $"{lanes.Rows.Sum(r => r.Up.Length):N0} edges, {(after - before) / 1024.0 / 1024.0:F1} MB held");

        sw.Restart();
        var relaid = new LaneAllocator(new LaneAllocator.Options(PrimaryTip(open), LaneAllocator.HintsFrom(log, lanes.Rows)));
        relaid.Add(log);
        Line("layout with hints", sw.Elapsed);

        if (log.Count > GraphLoader.PageSize)
        {
            Console.WriteLine($"\nPaging ({GraphLoader.PageSize} commits a page via --skip):");
            foreach (var skip in new[] { GraphLoader.PageSize, log.Count / 2, log.Count - GraphLoader.PageSize }.Distinct())
            {
                sw.Restart();
                var page = GraphLogReader.LoadLog(root, GraphLoader.PageSize, skip);
                Line($"skip {skip:N0}", sw.Elapsed, $"{page.Count} commits");
            }
        }
        GC.KeepAlive(lanes);
        GC.KeepAlive(log);
        return 0;
    }

    private static string? PrimaryTip(GraphSnapshot s) =>
        GraphLoader.PrimaryBranch(s.Refs) is { } p ? s.Refs.FirstOrDefault(r => r.Name == p)?.Target : null;

    private static void Stage(string stage, TimeSpan t) => Line(stage, t);

    private static void Line(string label, TimeSpan t, string extra = "") =>
        Console.WriteLine($"  {label,-22}{t.TotalMilliseconds,9:F1} ms  {extra}");
}

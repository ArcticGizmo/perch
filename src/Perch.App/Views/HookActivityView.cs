using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Perch.Avalonia.Rendering;
using Perch.Avalonia.Theming;
using Perch.Data.Resources;

namespace Perch.Avalonia.Views;

/// <summary>
/// The resource monitor's Hooks page (docs/resource-diagnostics-plan.md, "Hooks page"): one card per hook with its runs
/// per minute coloured by the event that fired them, a row per event (expand it for the sessions, agents and individual
/// runs), and what to change. One <see cref="Draw"/> routine measures and paints.
/// </summary>
internal sealed class HookActivityView : Control
{
    private const double Pad = 16, Size = 12.5, Small = 11, StripH = 40, Indent = 18;

    private IReadOnlyList<HookReport> _reports = [];
    private bool _hasActivity;
    private readonly HashSet<string> _openCards = new();
    private readonly HashSet<(string, string)> _openEvents = new();
    private readonly List<(Rect hit, string key)> _cardHits = new();
    private readonly List<(Rect hit, string card, string ev)> _eventHits = new();
    private readonly List<(Rect hit, string path)> _fileHits = new();
    private Point? _hover;

    /// <summary>Raised to open a hook's registration file.</summary>
    public event Action<string>? OpenFileRequested;

    public void SetReports(IReadOnlyList<HookReport> reports, bool hasActivity)
    {
        _reports = reports;
        _hasActivity = hasActivity;
        // The busiest hook starts open, so the page says something before any click.
        if (_openCards.Count == 0 && reports.Count > 0) _openCards.Add(reports[0].Cause.Key);
        InvalidateMeasure();
        InvalidateVisual();
    }

    /// <summary>Opens <paramref name="causeKey"/>'s card and its busiest event.</summary>
    public void FocusHook(string causeKey)
    {
        _openCards.Add(causeKey);
        if (_reports.FirstOrDefault(r => r.Cause.Key == causeKey) is { Events.Count: > 0 } r)
            _openEvents.Add((causeKey, r.Events[0].Event));
        InvalidateMeasure();
        InvalidateVisual();
    }

    /// <summary>The y of <paramref name="causeKey"/>'s card in the last layout, to scroll to it.</summary>
    public double? TopOf(string causeKey) => _cardHits.FirstOrDefault(c => c.key == causeKey) is { key: not null } c ? c.hit.Y : null;

    public static Color EventColor(string ev) => ev switch
    {
        "PreToolUse" => Palette.Accent,
        "PostToolUse" => Palette.Active.Teal.ToColor(),
        "PostToolUseFailure" => Palette.Red,
        "UserPromptSubmit" => Palette.Yellow,
        "Stop" => Palette.Orange,
        "SubagentStart" => Palette.Active.SubAgent.ToColor(),
        "SubagentStop" => Palette.Active.Burn.ToColor(),
        "SessionStart" or "SessionEnd" => Palette.Green,
        HookActivity.Unidentified => Palette.Muted,
        _ => Palette.Active.BackgroundTask.ToColor(),
    };

    protected override global::Avalonia.Size MeasureOverride(global::Avalonia.Size availableSize)
    {
        double w = double.IsFinite(availableSize.Width) && availableSize.Width > 0 ? availableSize.Width : 900;
        return new global::Avalonia.Size(w, Draw(null, w));
    }

    public override void Render(DrawingContext ctx)
    {
        ctx.FillRectangle(Palette.SurfaceSunkenBrush, new Rect(Bounds.Size));
        Draw(ctx, Bounds.Width);
    }

    private double Draw(DrawingContext? ctx, double width)
    {
        if (ctx is not null)
        {
            _cardHits.Clear();
            _eventHits.Clear();
            _fileHits.Clear();
        }
        double y = Pad;
        double inner = width - Pad * 2;

        var title = OverlayDraw.Text("Hook runs", 16, Palette.TitleBrush, FontWeight.Bold);
        ctx?.DrawText(title, new Point(Pad, y));
        y += title.Height + 4;
        y = Paragraph(ctx, "Claude Code doesn't log hook runs, so they're reconstructed. Runs: every event in the sessions' " +
                           "transcripts (each tool call before and after, prompts, turn ends, sub-agents starting and stopping) " +
                           "times the hooks registered for it. Caught: hook processes the 5 s sampler saw, matched to their event " +
                           "by start time. A hook no longer installed only has caught runs, its event inferred from timing.",
            Pad, y, inner, Palette.MutedBrush) + 12;

        if (_reports.Count == 0)
        {
            return Paragraph(ctx, _hasActivity
                ? "No hook runs in this recording."
                : "No transcript activity found for the recorded sessions, and no hook processes were caught.", Pad, y, inner, Palette.FgBrush) + Pad;
        }

        foreach (var report in _reports)
            y = Card(ctx, report, y, width) + 12;
        return y + Pad;
    }

    private double Card(DrawingContext? ctx, HookReport r, double top, double width)
    {
        bool open = _openCards.Contains(r.Cause.Key);
        double x = Pad, inner = width - Pad * 2, y = top;
        double headH = OverlayDraw.Text("Ag", 14, Palette.TitleBrush).Height + 14;
        var header = new Rect(x, y, inner, headH);
        if (ctx is not null)
        {
            _cardHits.Add((header, r.Cause.Key));
            bool hot = _hover is { } h && header.Contains(h);
            OverlayDraw.Panel(ctx, header, hot ? Palette.OverlayRowHoverBrush : Palette.FormBgBrush, OverlayDraw.Pen(Palette.BorderBrush, 1), 6);
            double mid = y + headH / 2;
            OverlayDraw.TextLeftMid(ctx, OverlayDraw.Text(open ? "▾" : "▸", Size, Palette.MutedBrush), x + 10, mid);
            string totals = $"{r.Runs:N0} run{(r.Runs == 1 ? "" : "s")} · {r.Seen:N0} caught" + (r.Errors > 0 ? $" · {r.Errors} failed" : "") +
                            (r.CpuSecondsSeen >= 0.5 ? $" · {r.CpuSecondsSeen:N0} CPU-s caught" : "");
            var totalsText = OverlayDraw.Text(totals, Size, r.Errors > 0 ? Palette.ErrorBrush : Palette.FgBrush);
            double totalsX = header.Right - 12 - totalsText.Width;
            var label = OverlayDraw.Text(OverlayDraw.Truncate(r.Cause.Label, 14, Math.Max(80, totalsX - x - 40), FontWeight.SemiBold), 14,
                Palette.TitleBrush, FontWeight.SemiBold);
            OverlayDraw.TextLeftMid(ctx, label, x + 26, mid);
            OverlayDraw.TextLeftMid(ctx, totalsText, totalsX, mid);
        }
        y += headH + 6;
        if (!open) return y;

        y = Strip(ctx, r, x + 10, y, inner - 20) + 8;

        double rowH = OverlayDraw.Text("Ag", Size, Palette.FgBrush).Height + 10;
        int maxRuns = Math.Max(1, r.Events.Max(e => e.Runs));
        foreach (var e in r.Events)
        {
            bool evOpen = _openEvents.Contains((r.Cause.Key, e.Event));
            var row = new Rect(x, y, inner, rowH);
            if (ctx is not null)
            {
                _eventHits.Add((row, r.Cause.Key, e.Event));
                if (_hover is { } h && row.Contains(h)) ctx.FillRectangle(Palette.OverlayRowHoverBrush, row);
                double mid = y + rowH / 2;
                OverlayDraw.TextLeftMid(ctx, OverlayDraw.Text(evOpen ? "▾" : "▸", Small, Palette.MutedBrush), x + 10, mid);
                OverlayDraw.Panel(ctx, new Rect(x + 24, mid - 5, 10, 10), OverlayDraw.Brush(EventColor(e.Event)), null, 2);
                string matcher = e.Matchers.Count == 0 ? "" : " " + string.Join(" ", e.Matchers.Select(m => m is "" or "*" ? "(every tool)" : $"({m})"));
                if (!ToolEvent(e.Event)) matcher = "";
                var name = OverlayDraw.Text(e.Event + matcher + (e.Inferred ? "  · inferred from timing" : ""), Size, Palette.FgBrush, FontWeight.SemiBold);
                OverlayDraw.TextLeftMid(ctx, name, x + 40, mid);

                double barX = x + 40 + Math.Max(name.Width + 12, 300), barW = Math.Max(60, inner * 0.18);
                var track = new Rect(barX, mid - 3, barW, 6);
                OverlayDraw.Pill(ctx, Palette.TrackBrush, track);
                OverlayDraw.Pill(ctx, OverlayDraw.Brush(EventColor(e.Event)), track.WithWidth(Math.Max(6, barW * e.Runs / maxRuns)));
                string counts = $"{e.Runs:N0} run{(e.Runs == 1 ? "" : "s")} · {e.Seen:N0} caught" + (e.Errors > 0 ? $" · {e.Errors} failed" : "") +
                                (e.AverageMs is { } ms ? $" · {ms:N0} ms avg" : "");
                var countsText = OverlayDraw.Text(counts, Small, Palette.FgBrush);
                ctx.DrawText(countsText, new Point(barX + barW + 10, mid - countsText.Height / 2));
                double toolsX = barX + barW + 10 + countsText.Width + 16;
                if (e.ByTool.Count > 0 && toolsX < row.Right - 80)
                {
                    string tools = string.Join(" · ", e.ByTool.Take(5).Select(t => $"{t.Name} {t.Runs}")) + (e.ByTool.Count > 5 ? " · …" : "");
                    var toolsText = OverlayDraw.Text(OverlayDraw.Truncate(tools, Small, row.Right - 10 - toolsX), Small, Palette.MutedBrush);
                    ctx.DrawText(toolsText, new Point(toolsX, mid - toolsText.Height / 2));
                }
            }
            y += rowH;
            if (evOpen) y = EventDetail(ctx, e, x + 40, y, inner - 40);
        }

        if (r.Advice.Count > 0)
        {
            y += 6;
            var head = OverlayDraw.Text("What you can do", Small, Palette.AccentBrush, FontWeight.SemiBold);
            ctx?.DrawText(head, new Point(x + 10, y));
            y += head.Height + 2;
            foreach (var a in r.Advice)
                y = Paragraph(ctx, "• " + a, x + 14, y, inner - 28, Palette.FgBrush) + 2;
        }
        if (r.Cause.Hook?.File is { Length: > 0 } file && LocalPath(file) is { } path)
        {
            y += 4;
            var link = OverlayDraw.Text(r.Cause.Hook.Kind == HookSourceKind.Plugin ? "Open the plugin's hooks.json" : "Open the settings file",
                Small, Palette.AccentBrush, FontWeight.SemiBold);
            var hit = new Rect(x + 14, y, link.Width, link.Height);
            if (ctx is not null)
            {
                ctx.DrawText(link, hit.TopLeft);
                ctx.DrawLine(OverlayDraw.Pen(Palette.AccentBrush, 1), new Point(hit.X, hit.Bottom), new Point(hit.Right, hit.Bottom));
                _fileHits.Add((hit, path));
            }
            y += link.Height + 2;
        }
        return y;
    }

    private static bool ToolEvent(string ev) => ev is "PreToolUse" or "PostToolUse" or "PostToolUseFailure" or "PermissionRequest";

    // Runs per minute, stacked by event, scaled to the busiest minute.
    private double Strip(DrawingContext? ctx, HookReport r, double x, double y, double w)
    {
        if (ctx is null) return y + StripH + OverlayDraw.Text("Ag", Small, Palette.MutedBrush).Height;
        var rect = new Rect(x, y, w, StripH);
        ctx.DrawRectangle(Palette.FormBgBrush, OverlayDraw.Pen(Palette.BorderBrush, 1), rect);
        int minutes = r.PerMinute.Count;
        int peak = Math.Max(1, r.PerMinute.Max(m => m.Sum()));
        double colW = w / Math.Max(1, minutes);
        for (int m = 0; m < minutes; m++)
        {
            double yb = rect.Bottom - 1;
            for (int e = 0; e < r.PerMinuteEvents.Count; e++)
            {
                double h = (StripH - 4) * r.PerMinute[m][e] / peak;
                if (h <= 0) continue;
                ctx.FillRectangle(OverlayDraw.Brush(EventColor(r.PerMinuteEvents[e]), 0.9),
                    new Rect(x + m * colW + 0.5, yb - h, Math.Max(1, colW - 1), h));
                yb -= h;
            }
        }
        var scale = OverlayDraw.Text($"peak {peak}/min", Small - 1, Palette.MutedBrush);
        ctx.DrawText(scale, new Point(rect.Right - scale.Width - 4, rect.Y + 2));
        var start = OverlayDraw.Text(r.StartUtc.ToLocalTime().ToString("HH:mm"), Small - 1, Palette.MutedBrush);
        ctx.DrawText(start, new Point(rect.X, rect.Bottom + 1));
        var end = OverlayDraw.Text(r.StartUtc.AddMinutes(minutes).ToLocalTime().ToString("HH:mm"), Small - 1, Palette.MutedBrush);
        ctx.DrawText(end, new Point(rect.Right - end.Width, rect.Bottom + 1));
        return rect.Bottom + start.Height;
    }

    private double EventDetail(DrawingContext? ctx, HookEventStats e, double x, double y, double w)
    {
        double lineH = OverlayDraw.Text("Ag", Small, Palette.FgBrush).Height + 4;
        y += 2;
        string by = "By session and agent: " + string.Join(" · ", e.ByAgent.Take(8).Select(a => $"{a.Name} {a.Runs}")) +
                    (e.ByAgent.Count > 8 ? $" · +{e.ByAgent.Count - 8} more" : "");
        y = Paragraph(ctx, by, x, y, w, Palette.MutedBrush) + 4;
        if (e.ByTool.Count > 5)
            y = Paragraph(ctx, "By tool: " + string.Join(" · ", e.ByTool.Select(t => $"{t.Name} {t.Runs}")), x, y, w, Palette.MutedBrush) + 4;

        var shown = e.Recent.Take(60).ToList();
        foreach (var run in shown)
        {
            if (ctx is not null)
            {
                double mid = y + lineH / 2;
                var time = OverlayDraw.Text(run.AtUtc.ToLocalTime().ToString("HH:mm:ss.f"), Small, Palette.MutedBrush);
                OverlayDraw.TextLeftMid(ctx, time, x, mid);
                string who = run.SessionLabel + (run.Agent is "main" or "?" ? "" : " · " + Agent(run.Agent));
                string what = (run.Tool is { } t ? t + "  ·  " : "") + who;
                double whatX = x + 92;
                string outcome = run.Error is { } err ? "failed: " + err
                    : run.Seen is { } s ? $"caught: pid {s.Pid}, {s.Processes} process{(s.Processes == 1 ? "" : "es")}"
                    : "not caught";
                if (run.DurationMs is { } ms) outcome += $" · {ms} ms";
                var outcomeText = OverlayDraw.Text(OverlayDraw.Truncate(outcome, Small, w * 0.45), Small,
                    run.Error is not null ? Palette.ErrorBrush : run.Seen is not null ? Palette.FgBrush : Palette.MutedBrush);
                double outX = x + w - outcomeText.Width;
                OverlayDraw.TextLeftMid(ctx, OverlayDraw.Text(OverlayDraw.Truncate(what, Small, Math.Max(60, outX - whatX - 12)), Small, Palette.FgBrush), whatX, mid);
                OverlayDraw.TextLeftMid(ctx, outcomeText, outX, mid);
            }
            y += lineH;
        }
        if (e.Recent.Count > shown.Count || e.Runs > e.Recent.Count)
            y = Paragraph(ctx, $"Latest {shown.Count} of {e.Runs:N0} runs shown.", x, y, w, Palette.MutedBrush) + 2;
        return y + 6;
    }

    private static string Agent(string agent) =>
        agent.StartsWith("agent-", StringComparison.Ordinal) && agent.Length > 12 ? "agent " + agent[6..12] : agent;

    private static double Paragraph(DrawingContext? ctx, string text, double x, double y, double w, IBrush brush)
    {
        var ft = OverlayDraw.NewText(text, Small + 0.5, brush);
        ft.MaxTextWidth = Math.Max(100, w);
        ctx?.DrawText(ft, new Point(x, y));
        return y + ft.Height;
    }

    private static string? LocalPath(string path)
    {
        var local = path.StartsWith('~') ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + path[1..] : path;
        return File.Exists(local) ? local : null;
    }

    // ── Input ────────────────────────────────────────────────────────────────

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        _hover = e.GetPosition(this);
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hover = null;
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var p = e.GetPosition(this);
        foreach (var (hit, path) in _fileHits)
        {
            if (!hit.Contains(p)) continue;
            OpenFileRequested?.Invoke(path);
            e.Handled = true;
            return;
        }
        foreach (var (hit, key) in _cardHits)
        {
            if (!hit.Contains(p)) continue;
            if (!_openCards.Remove(key)) _openCards.Add(key);
            Changed(e);
            return;
        }
        foreach (var (hit, card, ev) in _eventHits)
        {
            if (!hit.Contains(p)) continue;
            if (!_openEvents.Remove((card, ev))) _openEvents.Add((card, ev));
            Changed(e);
            return;
        }
    }

    private void Changed(PointerPressedEventArgs e)
    {
        e.Handled = true;
        InvalidateMeasure();
        InvalidateVisual();
    }
}

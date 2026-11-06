using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Perch.Avalonia.Rendering;
using Perch.Avalonia.Theming;
using Perch.Data.Resources;

namespace Perch.Avalonia.Views;

/// <summary>
/// What was using the machine at the resource monitor's cursor: a summary line, then one row per group (sortable
/// by CPU or memory) that expands into the group's process tree. One <see cref="Draw"/> routine measures and paints.
/// </summary>
internal sealed class ResourceBreakdownView : Control
{
    private const double Pad = 16, Size = 12.5, SmallSize = 11, ValueW = 150, Indent = 16;

    private ResourceAnalysis? _analysis;
    private int _tick = -1;
    private bool _byMemory;
    private string? _highlightGroup;
    private int? _highlightProc;
    private readonly HashSet<string> _expanded = new();

    private readonly List<(Rect hit, string groupKey)> _groupHits = new();
    private readonly List<(Rect hit, ResourceProc proc)> _procHits = new();
    private Rect _sortCpuHit, _sortMemHit;
    private Point? _hover;

    public ResourceBreakdownView()
    {
        Focusable = false;
    }

    public void SetData(ResourceAnalysis? analysis, int tick)
    {
        _analysis = analysis;
        _tick = tick;
        InvalidateMeasure();
        InvalidateVisual();
    }

    /// <summary>Opens a group and marks it (and optionally one of its processes), for a picked finding.</summary>
    public void Highlight(string? groupKey, int? procId)
    {
        _highlightGroup = groupKey;
        _highlightProc = procId;
        if (groupKey is not null) _expanded.Add(groupKey);
        InvalidateMeasure();
        InvalidateVisual();
    }

    protected override global::Avalonia.Size MeasureOverride(global::Avalonia.Size availableSize)
    {
        double w = double.IsFinite(availableSize.Width) && availableSize.Width > 0 ? availableSize.Width : 560;
        return new global::Avalonia.Size(w, Draw(null, w));
    }

    public override void Render(DrawingContext ctx)
    {
        ctx.FillRectangle(Palette.FormBgBrush, new Rect(Bounds.Size));
        Draw(ctx, Bounds.Width);
    }

    private double Draw(DrawingContext? ctx, double width)
    {
        if (ctx is not null)
        {
            _groupHits.Clear();
            _procHits.Clear();
        }

        double y = Pad;
        var a = _analysis;
        if (a is null || _tick < 0 || _tick >= a.TickCount)
        {
            var intro = Paragraph(a is null
                ? "Press Start to record what is using your CPU and memory. Perch samples every process on the machine every 5 seconds until you stop it (or 4 hours pass), then you can scrub through the recording here. Recordings stay on this machine; command lines are stored with tokens and your home folder masked."
                : "Waiting for the first sample…", width - Pad * 2);
            ctx?.DrawText(intro, new Point(Pad, y));
            return y + intro.Height + Pad;
        }

        var tick = a.Recording.Ticks[_tick];
        long totalRam = a.Recording.TotalRam;
        var title = OverlayDraw.Text(tick.AtUtc.ToLocalTime().ToString("HH:mm:ss"), 15, Palette.TitleBrush, FontWeight.Bold);
        string summary = $"CPU {ResourceFormat.Percent(tick.Cpu)}  ·  RAM {ResourceFormat.Bytes(tick.RamUsed)}"
                         + (totalRam > 0 ? $" of {ResourceFormat.Bytes(totalRam)}" : "")
                         + (tick.CommitLimit > 0 ? $"  ·  Commit {ResourceFormat.Bytes(tick.CommitUsed)} of {ResourceFormat.Bytes(tick.CommitLimit)}" : "");
        var sum = OverlayDraw.Text(summary, Size, Palette.FgBrush);
        if (ctx is not null)
        {
            ctx.DrawText(title, new Point(Pad, y));
            ctx.DrawText(sum, new Point(Pad + title.Width + 12, y + (title.Height - sum.Height) / 2));
            SortToggle(ctx, width, y + title.Height / 2);
        }
        y += title.Height + 10;

        var groups = a.BreakdownAt(_tick, _byMemory);
        double rowH = OverlayDraw.Text("Ag", Size, Palette.FgBrush).Height + 10;
        double procH = OverlayDraw.Text("Ag", SmallSize, Palette.FgBrush).Height + 6;
        double labelW = Math.Max(160, (width - Pad * 2 - ValueW) * 0.45);
        double barX = Pad + 34 + labelW, barW = Math.Max(20, width - Pad - ValueW - barX - 10);

        foreach (var g in groups)
        {
            var row = new Rect(0, y, width, rowH);
            bool expandable = g.Procs.Count > 0;
            bool open = expandable && _expanded.Contains(g.Group.Key);
            if (ctx is not null)
            {
                if (g.Group.Key == _highlightGroup)
                    ctx.FillRectangle(OverlayDraw.Brush(Palette.Accent, 0.12), row);
                else if (_hover is { } h && row.Contains(h) && expandable)
                    ctx.FillRectangle(Palette.OverlayRowHoverBrush, row);

                double mid = y + rowH / 2;
                if (expandable)
                    OverlayDraw.TextLeftMid(ctx, OverlayDraw.Text(open ? "▾" : "▸", Size, Palette.MutedBrush), Pad, mid);
                int band = a.ChartIndexOf(g.Group.Key);
                var swatchGroup = a.Charted[band];
                OverlayDraw.Panel(ctx, new Rect(Pad + 14, mid - 5, 12, 10),
                    OverlayDraw.Brush(ResourceTimelineView.BandColor(swatchGroup, band),
                        Math.Min(1, ResourceTimelineView.BandOpacity(swatchGroup) + 0.1)), null, 2);
                var label = OverlayDraw.Text(OverlayDraw.Truncate(g.Group.Label, Size, labelW, FontWeight.SemiBold), Size,
                    Palette.FgBrush, FontWeight.SemiBold);
                OverlayDraw.TextLeftMid(ctx, label, Pad + 34, mid);

                double share = _byMemory
                    ? (totalRam > 0 ? (double)g.Memory / totalRam : 0)
                    : g.Cpu / 100.0;
                var track = new Rect(barX, mid - 3, barW, 6);
                OverlayDraw.Pill(ctx, Palette.TrackBrush, track);
                OverlayDraw.Pill(ctx, OverlayDraw.Brush(ResourceTimelineView.BandColor(swatchGroup, band)),
                    track.WithWidth(Math.Max(track.Height, barW * Math.Clamp(share, 0, 1))));

                // The OS's rows measure one thing each: interrupts are CPU, the pools and caches are memory.
                string figures = g.Group.Kind != ResourceGroupKind.System
                    ? $"{ResourceFormat.Percent(g.Cpu)}  ·  {ResourceFormat.Bytes(g.Memory)}"
                    : g.Group == ResourceAnalysis.Interrupts ? ResourceFormat.Percent(g.Cpu) : ResourceFormat.Bytes(g.Memory);
                var value = OverlayDraw.Text(figures, Size, Palette.FgBrush);
                OverlayDraw.TextLeftMid(ctx, value, width - Pad - value.Width, mid);
                if (expandable) _groupHits.Add((row, g.Group.Key));
            }
            y += rowH;

            if (!open) continue;
            // A session first says why its processes exist (hooks, tool calls, search…), over the whole recording:
            // most of them live for less than a sample, so the moment alone hides them.
            if (g.Group.Kind == ResourceGroupKind.Session && a.CausesAt(g.Group.Key, _tick) is { Count: > 0 } causes)
            {
                y = SubHeader(ctx, "What it launched, whole recording (seen; quick processes are often missed)", y, procH);
                foreach (var c in causes)
                {
                    if (ctx is not null) PaintCause(ctx, c, y, procH, width);
                    y += procH;
                }
                y = SubHeader(ctx, "Processes alive at this moment", y, procH);
            }
            foreach (var p in g.Procs)
            {
                if (ctx is not null) PaintProc(ctx, p, y, procH, width);
                y += procH;
            }
            y += 4;
        }

        return y + Pad;
    }

    private void PaintProc(DrawingContext ctx, BreakdownProc p, double y, double h, double width)
    {
        var row = new Rect(0, y, width, h);
        if (p.Proc.Id == _highlightProc)
            ctx.FillRectangle(OverlayDraw.Brush(Palette.Accent, 0.18), row);
        else if (_hover is { } hv && row.Contains(hv))
            ctx.FillRectangle(Palette.OverlayRowHoverBrush, row);

        double mid = y + h / 2;
        double x = Pad + 34 + Indent * p.Depth;
        if (p.Depth > 0)
            ctx.DrawLine(OverlayDraw.Pen(Palette.TreeLineBrush, 1), new Point(x - 10, mid), new Point(x - 4, mid));

        // Shared pages aren't in the process's own figure (they're counted once, under Unattributed), but a process
        // mapping a lot of them is worth seeing.
        string shared = p.Sample.Shared >= 16L * 1024 * 1024 ? $"  (+{ResourceFormat.Bytes(p.Sample.Shared)} shared)" : "";
        var value = OverlayDraw.Text(
            $"{ResourceFormat.Percent(p.Sample.Cpu)}  ·  {ResourceFormat.Bytes(p.Sample.Memory)}{shared}", SmallSize, Palette.FgBrush);
        double valueX = width - Pad - value.Width;

        string io = p.Sample.IoReadPerSec + p.Sample.IoWritePerSec >= 1024 * 1024
            ? $"  disk {ResourceFormat.Rate(p.Sample.IoReadPerSec + p.Sample.IoWritePerSec)}"
            : "";
        var name = OverlayDraw.Text($"{ResourceAnalysis.ToolLabel(p.Proc)} ({p.Proc.Pid}){io}", SmallSize, Palette.FgBrush);
        double nameW = Math.Min(name.Width, Math.Max(40, valueX - x - 12));
        var nameText = nameW < name.Width
            ? OverlayDraw.Text(OverlayDraw.Truncate($"{ResourceAnalysis.ToolLabel(p.Proc)} ({p.Proc.Pid}){io}", SmallSize, nameW), SmallSize, Palette.FgBrush)
            : name;
        OverlayDraw.TextLeftMid(ctx, nameText, x, mid);

        // The arguments, not the exe path: the name column already says what ran.
        if (Arguments(p.Proc.CommandLine) is { Length: > 0 } args)
        {
            double argsX = x + nameText.Width + 10, argsW = valueX - argsX - 12;
            if (argsW > 40)
                OverlayDraw.TextLeftMid(ctx, OverlayDraw.Text(OverlayDraw.Truncate(args, SmallSize, argsW), SmallSize, Palette.MutedBrush), argsX, mid);
        }
        OverlayDraw.TextLeftMid(ctx, value, valueX, mid);
        _procHits.Add((row, p.Proc));
    }

    private static double SubHeader(DrawingContext? ctx, string text, double y, double h)
    {
        if (ctx is not null)
            OverlayDraw.TextLeftMid(ctx, OverlayDraw.Text(text, SmallSize - 0.5, Palette.MutedBrush, FontWeight.SemiBold), Pad + 34, y + h / 2 + 1);
        return y + h;
    }

    private static void PaintCause(DrawingContext ctx, CauseAt c, double y, double h, double width)
    {
        double mid = y + h / 2;
        double x = Pad + 34 + Indent;
        var cause = c.Stats.Cause;
        bool notable = cause.Kind is LaunchCauseKind.Hook or LaunchCauseKind.Command or LaunchCauseKind.StatusLine && c.Stats.Launches >= 20;
        var value = OverlayDraw.Text(c.Alive > 0 ? $"{ResourceFormat.Percent(c.Cpu)}  ·  {ResourceFormat.Bytes(c.Memory)}" : "—",
            SmallSize, c.Alive > 0 ? Palette.FgBrush : Palette.MutedBrush);
        double valueX = width - Pad - value.Width;
        string counts = cause.Kind == LaunchCauseKind.Claude
            ? $"{c.Stats.CpuSeconds:N0} CPU-s"
            : $"{c.Stats.Launches}+ launches · {c.Stats.Processes} processes · {c.Stats.CpuSeconds:N0} CPU-s";
        var countsText = OverlayDraw.Text(counts, SmallSize, Palette.MutedBrush);
        double countsX = valueX - 16 - countsText.Width;
        var label = OverlayDraw.Text(OverlayDraw.Truncate(cause.Label, SmallSize, Math.Max(40, countsX - x - 10), FontWeight.SemiBold),
            SmallSize, notable ? Palette.AttentionBrush : Palette.FgBrush, FontWeight.SemiBold);
        OverlayDraw.TextLeftMid(ctx, label, x, mid);
        if (countsX > x + label.Width + 6) OverlayDraw.TextLeftMid(ctx, countsText, countsX, mid);
        OverlayDraw.TextLeftMid(ctx, value, valueX, mid);
    }

    private void SortToggle(DrawingContext ctx, double width, double mid)
    {
        var mem = OverlayDraw.Text("Memory", SmallSize, _byMemory ? Palette.OnAccentBrush : Palette.FgBrush);
        var cpu = OverlayDraw.Text("CPU", SmallSize, _byMemory ? Palette.FgBrush : Palette.OnAccentBrush);
        double h = mem.Height + 6;
        _sortMemHit = new Rect(width - Pad - mem.Width - 16, mid - h / 2, mem.Width + 16, h);
        _sortCpuHit = new Rect(_sortMemHit.X - cpu.Width - 16 - 4, mid - h / 2, cpu.Width + 16, h);
        var border = OverlayDraw.Pen(Palette.BorderBrush, 1);
        OverlayDraw.Panel(ctx, _sortCpuHit, _byMemory ? Palette.ButtonBgBrush : Palette.AccentBrush, border, 5);
        OverlayDraw.Panel(ctx, _sortMemHit, _byMemory ? Palette.AccentBrush : Palette.ButtonBgBrush, border, 5);
        ctx.DrawText(cpu, new Point(_sortCpuHit.X + 8, mid - cpu.Height / 2));
        ctx.DrawText(mem, new Point(_sortMemHit.X + 8, mid - mem.Height / 2));
        var sort = OverlayDraw.Text("Sort", SmallSize, Palette.MutedBrush);
        ctx.DrawText(sort, new Point(_sortCpuHit.X - sort.Width - 8, mid - sort.Height / 2));
    }

    private static FormattedText Paragraph(string text, double width)
    {
        var ft = OverlayDraw.NewText(text, 13, Palette.MutedBrush);
        ft.MaxTextWidth = Math.Max(100, width);
        return ft;
    }

    /// <summary>The command line minus its leading executable (quoted or not).</summary>
    internal static string Arguments(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return "";
        var s = commandLine.TrimStart();
        int end = s.StartsWith('"') ? s.IndexOf('"', 1) + 1 : s.IndexOf(' ');
        return end <= 0 || end >= s.Length ? "" : s[end..].Trim();
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
        var point = e.GetCurrentPoint(this);
        var p = point.Position;

        if (point.Properties.IsRightButtonPressed)
        {
            foreach (var (hit, proc) in _procHits)
            {
                if (!hit.Contains(p)) continue;
                ShowProcMenu(proc);
                e.Handled = true;
                return;
            }
            return;
        }
        if (!point.Properties.IsLeftButtonPressed) return;

        if (_sortCpuHit.Contains(p) || _sortMemHit.Contains(p))
        {
            _byMemory = _sortMemHit.Contains(p);
            Changed();
            e.Handled = true;
            return;
        }
        foreach (var (hit, key) in _groupHits)
        {
            if (!hit.Contains(p)) continue;
            if (!_expanded.Remove(key)) _expanded.Add(key);
            Changed();
            e.Handled = true;
            return;
        }
    }

    private void Changed()
    {
        InvalidateMeasure();
        InvalidateVisual();
    }

    private void ShowProcMenu(ResourceProc proc)
    {
        var items = new List<Control>();
        void Copy(string header, string text)
        {
            var item = new MenuItem { Header = header };
            item.Click += async (_, _) =>
            {
                try
                {
                    if (TopLevel.GetTopLevel(this)?.Clipboard is { } clip) await clip.SetTextAsync(text);
                }
                catch { }
            };
            items.Add(item);
        }
        if (!string.IsNullOrEmpty(proc.CommandLine)) Copy("Copy command line", proc.CommandLine);
        Copy("Copy process ID", proc.Pid.ToString());
        Copy("Copy name", proc.Name);
        new MenuFlyout { ItemsSource = items }.ShowAt(this, true);
    }
}

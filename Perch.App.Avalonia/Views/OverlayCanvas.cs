using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Perch.Avalonia.Rendering;
using Perch.Avalonia.Theming;
using Perch.Data;

namespace Perch.Avalonia.Views;

/// <summary>
/// The owner-drawn overlay body — the Avalonia port of <c>OverlayForm</c>'s painting, replacing the
/// thin-vertical XAML <c>OverlayView</c>. A single <see cref="Draw"/> routine both measures (returns the
/// content height when given a null context) and paints (when given a real one), so the measured height
/// and painted layout can never drift — the same measure-or-paint discipline the WinForms dashboards use.
///
/// Built up over Phase 4. Done so far: rounded panel (4.1); header/collapsed bar (4.2); expanded session
/// rows (4.3); sub-agents, teammates, and the collapsible "Autonomous" section (4.4). Per-row glyphs,
/// bars, and interaction follow.
/// </summary>
public sealed class OverlayCanvas : Control
{
    // ── Layout (mirrors OverlayForm's constants) ──────────────────────────────
    private const double FormWidth        = 280;
    private const double HeaderHeight     = 44;
    private const double Corner           = 10;
    private const double HorizPad         = 12;
    private const double IconBoxW         = 16;
    private const double IconBoxH         = 16;
    private const double IconGap          = 6;
    private const double RowHeight        = 46;
    private const double SubRowHeight     = 24;
    private const double SectionRowHeight = 26;
    private const double SubIndent        = 22;
    private const double BotIconWidth     = 16;
    private const double RcIconWidth      = 14;
    private const double MailIconWidth    = 16;
    private const double ModeBadgeWidth   = 16;
    private const double RowsTop          = HeaderHeight; // usage/metrics/quick-links strips inserted here later

    // Font sizes (px ~= the WinForms point sizes).
    private const double NameSize       = 11.5;
    private const double StatusSize     = 10;
    private const double ActivitySize   = 10;
    private const double SubNameSize    = 11;
    private const double SubStatusSize  = 9.5;
    private const double SectionLabel   = 10;
    private const double SectionChev    = 9;

    // ── Palette (the overlay's own; matches OverlayForm) ──────────────────────
    private static readonly Color  BgColor        = Color.FromRgb(15, 15, 20);
    private static readonly Color  FgColor        = Color.FromRgb(225, 225, 235);
    private static readonly IBrush BgBrush        = new SolidColorBrush(Color.FromArgb(245, 15, 15, 20));
    private static readonly IPen   BorderPen      = new Pen(new SolidColorBrush(Color.FromRgb(45, 45, 60)), 1);
    private static readonly IBrush MutedBrush     = new SolidColorBrush(Color.FromRgb(110, 110, 130));
    private static readonly IBrush FgBrush        = new SolidColorBrush(FgColor);
    private static readonly Color  RunningColor   = Color.FromRgb(34, 197, 94);
    private static readonly Color  AttentionColor = Color.FromRgb(251, 146, 60);
    private static readonly Color  AwaitingColor  = Color.FromRgb(250, 204, 21);
    private static readonly Color  IdleColor      = Color.FromRgb(100, 116, 139);
    private static readonly IBrush AttentionBrush = new SolidColorBrush(AttentionColor);
    private static readonly IBrush AwaitingBrush  = new SolidColorBrush(AwaitingColor);
    private static readonly IPen   SepPen         = new Pen(new SolidColorBrush(Color.FromRgb(35, 35, 50)), 1);
    private static readonly Color  SubAgentColor  = Color.FromRgb(168, 85, 247);
    private static readonly IBrush SubAgentBrush  = new SolidColorBrush(SubAgentColor);
    private static readonly IPen   TreeLinePen    = new Pen(new SolidColorBrush(Color.FromRgb(55, 55, 72)), 1);
    private static readonly IBrush BotBrush       = new SolidColorBrush(Color.FromRgb(148, 163, 184));
    private static readonly IBrush BadgeBrush     = new SolidColorBrush(Color.FromRgb(38, 38, 52));
    private static readonly Color  MailColor      = Color.FromRgb(94, 234, 212);
    private static readonly IBrush MailBrush      = new SolidColorBrush(MailColor);
    private static readonly Color  RemoteColor    = Color.FromRgb(96, 165, 250);
    private static readonly IBrush RemoteBrush    = new SolidColorBrush(RemoteColor);

    // Brand mark (the app icon), loaded once.
    private static readonly Bitmap? Brand = LoadBrand();

    // "Waiting on you" ramp length (minutes to fully red); user-tunable later (SetWaitingTimerRedMinutes).
    private int _waitingTimerRedMinutes = 3;

    // Display gates (wired to Settings in 4.17).
    private bool _hideInactiveTeamMembers;
    private bool _showModeBadges = true;

    /// <summary>Show/hide the permission-mode badge on session rows.</summary>
    public void SetShowModeBadges(bool show)
    {
        if (_showModeBadges == show) return;
        _showModeBadges = show;
        InvalidateVisual();
    }

    private IReadOnlyList<ClaudeSession> _sessions = [];
    private List<DisplayRow> _rows = [];
    private bool _expanded = true;
    private bool _autonomousExpanded;

    // A flat render row: a parent session, one of its sub-agents, or the "Autonomous" section header.
    private readonly record struct DisplayRow(ClaudeSession? Session, SubAgent? Sub, int SectionCount = -1)
    {
        public bool IsSubAgent => Sub != null;
        public bool IsSectionHeader => SectionCount >= 0;
    }

    /// <summary>When on, idle teammates are dropped from the roster (only working ones show). Wired to
    /// the Settings toggle in 4.17; a hidden teammate reappears the moment it starts working again.</summary>
    public void SetHideInactiveTeamMembers(bool hide)
    {
        if (_hideInactiveTeamMembers == hide) return;
        _hideInactiveTeamMembers = hide;
        Update(_sessions); // rebuild the render list under the new gate
    }

    /// <summary>Feeds the latest session list (on the UI thread) and rebuilds the render list.</summary>
    public void Update(IReadOnlyList<ClaudeSession> sessions)
    {
        _sessions = sessions;

        // Interactive sessions render at the top; background/SDK-driven ones group under the
        // collapsible "Autonomous" section. Each partition sorted by display name.
        var interactive = sessions.Where(s => !s.IsBackground)
            .OrderBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase);
        var background = sessions.Where(s => s.IsBackground)
            .OrderBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();

        var rows = new List<DisplayRow>();
        foreach (var s in interactive) AddSessionRows(rows, s);

        if (background.Count > 0)
        {
            rows.Add(new DisplayRow(null, null, background.Count));
            if (_autonomousExpanded)
                foreach (var s in background) AddSessionRows(rows, s);
        }
        else _autonomousExpanded = false;

        _rows = rows;
        if (sessions.Count == 0) _expanded = false;

        InvalidateMeasure();
        InvalidateVisual();
    }

    // A session's row plus its running sub-agent / teammate child rows, in draw order.
    private void AddSessionRows(List<DisplayRow> rows, ClaudeSession session)
    {
        rows.Add(new DisplayRow(session, null));
        var ordered = session.SubAgents
            .Where(s => !_hideInactiveTeamMembers || !(s.IsTeammate && s.IsIdle))
            .OrderByDescending(s => s.IsTeammate)
            .ThenBy(s => s.IsTeammate && s.IsIdle)
            .ThenBy(s => s.Name ?? s.Description, StringComparer.OrdinalIgnoreCase);
        foreach (var sub in ordered) rows.Add(new DisplayRow(session, sub));
    }

    private static double HeightOf(DisplayRow row) =>
        row.IsSectionHeader ? SectionRowHeight : row.IsSubAgent ? SubRowHeight : RowHeight;

    protected override Size MeasureOverride(Size availableSize)
        => new(FormWidth, Draw(null, FormWidth));

    public override void Render(DrawingContext ctx) => Draw(ctx, Bounds.Width);

    // Measure-or-paint: returns the content height; paints only when ctx is non-null.
    private double Draw(DrawingContext? ctx, double width)
    {
        bool showRows = _expanded && _rows.Count > 0;
        double height = HeaderHeight;
        if (showRows)
        {
            foreach (var r in _rows) height += HeightOf(r);
            height += 2;
        }

        if (ctx != null)
        {
            OverlayDraw.Panel(ctx, new Rect(0.5, 0.5, width - 1, height - 1), BgBrush, BorderPen, Corner);
            DrawHeader(ctx, width);

            if (showRows)
            {
                double top = RowsTop;
                foreach (var r in _rows)
                {
                    if (r.IsSectionHeader) DrawSectionHeaderRow(ctx, r, top, width);
                    else if (r.IsSubAgent) DrawSubAgentRow(ctx, r.Sub!, top, width);
                    else DrawSessionRow(ctx, r.Session!, top, width);
                    top += HeightOf(r);
                }
            }
        }

        return height;
    }

    private void DrawHeader(DrawingContext ctx, double width)
    {
        double midY = HeaderHeight / 2;

        double brandRight = HorizPad;
        if (Brand is { })
        {
            const double iconSize = 18;
            ctx.DrawImage(Brand, new Rect(HorizPad, midY - iconSize / 2, iconSize, iconSize));
            brandRight = HorizPad + iconSize + 5;
        }

        var label = OverlayDraw.Text("Perch", 11, MutedBrush);
        OverlayDraw.TextLeftMid(ctx, label, brandRight, midY);
        brandRight += label.Width;

        double sepX = brandRight + 4;
        ctx.DrawEllipse(MutedBrush, null, new Point(sepX + 2, midY), 2, 2);
        double x = sepX + 10;

        if (_sessions.Count == 0)
        {
            OverlayDraw.TextLeftMid(ctx, OverlayDraw.Text("no sessions", 11, MutedBrush), x, midY);
        }
        else
        {
            int running   = _sessions.Count(s => s.Status == SessionStatus.Running);
            int attention = _sessions.Count(s => s.Status == SessionStatus.NeedsAttention);
            int awaiting  = _sessions.Count(s => s.Status == SessionStatus.AwaitingInput);
            int idle      = _sessions.Count(s => s.Status == SessionStatus.Idle);

            x = DrawStatusPill(ctx, x, midY, awaiting,  AwaitingColor,  AwaitingColor);
            x = DrawStatusPill(ctx, x, midY, running,   RunningColor,   FgColor);
            x = DrawStatusPill(ctx, x, midY, attention, AttentionColor, AttentionColor);
            if (running == 0 && attention == 0 && awaiting == 0)
                DrawStatusPill(ctx, x, midY, idle, IdleColor, IdleColor);
        }

        // Right cluster: dense toggle (drawn; click wiring 4.12) + expand chevron. Update badge is 4.14.
        DrawSideCollapseIcon(ctx, SideIconRect(width), reversed: false);
        double clusterLeft = width - HorizPad - IconBoxW;

        if (_sessions.Count > 0)
        {
            var chevron = OverlayDraw.Text(_expanded ? "▲" : "▼", 9, MutedBrush);
            double chevX = clusterLeft - IconGap - chevron.Width;
            OverlayDraw.TextLeftMid(ctx, chevron, chevX, midY);
        }
    }

    private Rect SideIconRect(double width)
    {
        double top = (HeaderHeight - IconBoxH) / 2;
        return new Rect(width - HorizPad - IconBoxW, top, IconBoxW, IconBoxH);
    }

    private static double DrawStatusPill(DrawingContext ctx, double x, double midY, int count,
                                         Color dotColor, Color textColor)
    {
        if (count == 0) return x;
        ctx.DrawEllipse(new SolidColorBrush(dotColor), null, new Point(x + 4, midY), 4, 4);
        x += 12;
        var label = OverlayDraw.Text(count.ToString(), 12, new SolidColorBrush(textColor), FontWeight.Bold);
        OverlayDraw.TextLeftMid(ctx, label, x, midY);
        return x + label.Width + 8;
    }

    private static void DrawSideCollapseIcon(DrawingContext ctx, Rect r, bool reversed)
    {
        var pen = new Pen(MutedBrush, 1.6, lineCap: PenLineCap.Round);
        double midY = r.Top + r.Height / 2;
        double pad = 3, left = r.Left + pad, right = r.Right - pad, headLen = 4;

        if (!reversed)
        {
            double pipeX = right, shaftEnd = pipeX - 2;
            ctx.DrawLine(pen, new Point(left, midY), new Point(shaftEnd, midY));
            ctx.DrawLine(pen, new Point(shaftEnd - headLen, midY - headLen), new Point(shaftEnd, midY));
            ctx.DrawLine(pen, new Point(shaftEnd - headLen, midY + headLen), new Point(shaftEnd, midY));
            ctx.DrawLine(pen, new Point(pipeX, r.Top + pad), new Point(pipeX, r.Bottom - pad));
        }
        else
        {
            double pipeX = left, shaftEnd = pipeX + 2;
            ctx.DrawLine(pen, new Point(right, midY), new Point(shaftEnd, midY));
            ctx.DrawLine(pen, new Point(shaftEnd + headLen, midY - headLen), new Point(shaftEnd, midY));
            ctx.DrawLine(pen, new Point(shaftEnd + headLen, midY + headLen), new Point(shaftEnd, midY));
            ctx.DrawLine(pen, new Point(pipeX, r.Top + pad), new Point(pipeX, r.Bottom - pad));
        }
    }

    // ── Session row (core) ────────────────────────────────────────────────────
    private void DrawSessionRow(DrawingContext ctx, ClaudeSession session, double top, double width)
    {
        ctx.DrawLine(SepPen, new Point(HorizPad, top), new Point(width - HorizPad, top));

        double midY = top + RowHeight / 2;
        bool running  = session.Status == SessionStatus.Running;
        bool awaiting = session.Status == SessionStatus.AwaitingInput;

        string? activity = running ? session.Activity : awaiting ? "waiting on you" : null;
        string? elapsed  = running ? session.RunningElapsedLabel()
                         : awaiting ? session.AwaitingElapsedLabel() : null;
        bool twoLine = !string.IsNullOrEmpty(activity) || !string.IsNullOrEmpty(elapsed);
        double nameMidY = twoLine ? top + RowHeight / 2 - 8 : midY;

        IBrush secondLine = awaiting
            ? new SolidColorBrush(WarmWaitingColor(session.AwaitingElapsed() ?? TimeSpan.Zero))
            : MutedBrush;

        var dotColor = session.Status switch
        {
            SessionStatus.Running        => RunningColor,
            SessionStatus.NeedsAttention => AttentionColor,
            SessionStatus.AwaitingInput  => AwaitingColor,
            _                            => IdleColor,
        };
        ctx.DrawEllipse(new SolidColorBrush(dotColor), null, new Point(HorizPad + 4, nameMidY), 4, 4);

        string statusText = session.Status switch
        {
            SessionStatus.Running        => "running",
            SessionStatus.NeedsAttention => "done ↩",
            SessionStatus.AwaitingInput  => "input ↩",
            _                            => "idle",
        };
        IBrush statusBrush = session.Status switch
        {
            SessionStatus.NeedsAttention => AttentionBrush,
            SessionStatus.AwaitingInput  => AwaitingBrush,
            _                            => MutedBrush,
        };

        double statusW = OverlayDraw.MeasureWidth(statusText, StatusSize);

        // Left-of-name glyph cluster (warn/artifact/party slots are reserved 0-width until 4.6/4.7).
        const double warnW = 0, artW = 0, partyW = 0;
        double mailW = session.ExternalNotify ? MailIconWidth : 0;
        double rcW   = session.RemoteControlled ? RcIconWidth : 0;
        double botW  = session.IsBackground ? BotIconWidth : 0;

        // Right side: the permission-mode badge, just left of the status text.
        bool showBadge = session.Mode != PermissionMode.Normal && _showModeBadges;
        double badgeW = showBadge ? ModeBadgeWidth : 0;

        double nameMax = width - HorizPad * 3 - 8 - statusW - badgeW - rcW - botW - mailW;
        string nameTrunc = OverlayDraw.Truncate(session.DisplayName, NameSize, nameMax);

        if (mailW > 0) DrawMailIcon(ctx, HorizPad + 14 + warnW + artW, nameMidY);
        if (rcW > 0)   DrawRemoteIcon(ctx, HorizPad + 16 + warnW + artW + mailW, nameMidY);
        if (botW > 0)  DrawBotIcon(ctx, HorizPad + 14 + warnW + artW + mailW + rcW + partyW, nameMidY);

        double nameX = HorizPad + 14 + warnW + artW + mailW + rcW + partyW + botW;
        OverlayDraw.TextLeftMid(ctx, OverlayDraw.Text(nameTrunc, NameSize, FgBrush), nameX, nameMidY);

        double statusX = width - HorizPad - statusW;
        OverlayDraw.TextLeftMid(ctx, OverlayDraw.Text(statusText, StatusSize, statusBrush), statusX, nameMidY);

        if (showBadge)
        {
            int alpha = session.Status == SessionStatus.Idle ? 110 : 255;
            DrawModeBadge(ctx, session.Mode, statusX - badgeW, nameMidY, alpha);
        }

        if (twoLine)
        {
            double activityMidY = top + RowHeight / 2 + 9;
            double lineLeft = HorizPad + 14;

            double elapsedW = 0;
            if (!string.IsNullOrEmpty(elapsed))
            {
                elapsedW = OverlayDraw.MeasureWidth(elapsed, ActivitySize);
                OverlayDraw.TextLeftMid(ctx, OverlayDraw.Text(elapsed, ActivitySize, secondLine),
                    width - HorizPad - elapsedW, activityMidY);
            }
            if (!string.IsNullOrEmpty(activity))
            {
                double activityMax = width - lineLeft - HorizPad - (elapsedW > 0 ? elapsedW + 6 : 0);
                string actTrunc = OverlayDraw.Truncate(activity, ActivitySize, activityMax);
                OverlayDraw.TextLeftMid(ctx, OverlayDraw.Text(actTrunc, ActivitySize, secondLine),
                    lineLeft, activityMidY);
            }
        }
    }

    private Color WarmWaitingColor(TimeSpan waited)
    {
        double fullMinutes = Math.Max(1, _waitingTimerRedMinutes);
        double t = Math.Clamp(waited.TotalMinutes / fullMinutes, 0.0, 1.0);
        var to = Color.FromRgb(239, 68, 68);
        byte Lerp(byte a, byte b) => (byte)Math.Round(a + (b - a) * t);
        return Color.FromRgb(Lerp(AwaitingColor.R, to.R), Lerp(AwaitingColor.G, to.G), Lerp(AwaitingColor.B, to.B));
    }

    // ── "Autonomous" section header ───────────────────────────────────────────
    private void DrawSectionHeaderRow(DrawingContext ctx, DisplayRow row, double top, double width)
    {
        double midY = top + SectionRowHeight / 2;

        var chevron = OverlayDraw.Text(_autonomousExpanded ? "▾" : "▸", SectionChev, MutedBrush);
        double x = HorizPad;
        OverlayDraw.TextLeftMid(ctx, chevron, x, midY);
        x += chevron.Width + 4;

        DrawBotIcon(ctx, x, midY);
        x += BotIconWidth;

        var label = OverlayDraw.Text("Autonomous", SectionLabel, MutedBrush);
        OverlayDraw.TextLeftMid(ctx, label, x, midY);
        x += label.Width + 6;

        // Count badge (dim pill).
        var countFt = OverlayDraw.Text(row.SectionCount.ToString(), SectionLabel, MutedBrush);
        double badgeW = countFt.Width + 10;
        double badgeH = label.Height + 2;
        OverlayDraw.Panel(ctx, new Rect(x, midY - badgeH / 2, badgeW, badgeH), BadgeBrush, null, badgeH / 2);
        OverlayDraw.TextLeftMid(ctx, countFt, x + 5, midY);
        x += badgeW + 8;

        if (x < width - HorizPad)
            ctx.DrawLine(SepPen, new Point(x, midY), new Point(width - HorizPad, midY));
    }

    // ── Sub-agent / teammate rows ─────────────────────────────────────────────
    private void DrawSubAgentRow(DrawingContext ctx, SubAgent sub, double top, double width)
    {
        double midY = top + SubRowHeight / 2;

        // Tree connector: a stub dropping from the parent row down to this child's marker.
        double branchX = HorizPad + 4;
        double markerX = HorizPad + SubIndent;
        ctx.DrawLine(TreeLinePen, new Point(branchX, top - SubRowHeight / 2), new Point(branchX, midY));
        ctx.DrawLine(TreeLinePen, new Point(branchX, midY), new Point(markerX - 2, midY));

        if (sub.IsTeammate) DrawTeammateRow(ctx, sub, markerX, midY, width);
        else DrawPlainSubAgentRow(ctx, sub, markerX, midY, width);
    }

    private void DrawPlainSubAgentRow(DrawingContext ctx, SubAgent sub, double dotX, double midY, double width)
    {
        ctx.DrawEllipse(SubAgentBrush, null, new Point(dotX + 3, midY), 3, 3);

        const string statusText = "running";
        double statusW = OverlayDraw.MeasureWidth(statusText, SubStatusSize);
        double labelX = dotX + 12;
        double labelMaxW = width - labelX - HorizPad - statusW - 6;

        string type = sub.AgentType?.Trim() ?? "";
        string desc = sub.Description?.Trim() ?? "";
        if (string.Equals(desc, type, StringComparison.Ordinal)) desc = "";
        if (type.Length == 0 && desc.Length == 0) desc = "sub-agent";

        double x = labelX;
        if (type.Length > 0)
        {
            string typeTrunc = OverlayDraw.Truncate(type, SubNameSize, labelMaxW / 2);
            OverlayDraw.TextLeftMid(ctx, OverlayDraw.Text(typeTrunc, SubNameSize, MutedBrush), x, midY);
            x += OverlayDraw.MeasureWidth(typeTrunc, SubNameSize) + 8;
        }
        if (desc.Length > 0)
        {
            string descTrunc = OverlayDraw.Truncate(desc, SubNameSize, labelMaxW - (x - labelX));
            OverlayDraw.TextLeftMid(ctx, OverlayDraw.Text(descTrunc, SubNameSize, FgBrush), x, midY);
        }

        OverlayDraw.TextLeftMid(ctx, OverlayDraw.Text(statusText, SubStatusSize, SubAgentBrush),
            width - HorizPad - statusW, midY);
    }

    private void DrawTeammateRow(DrawingContext ctx, SubAgent sub, double glyphX, double midY, double width)
    {
        bool idle = sub.IsIdle;
        Color teamColor = Palette.TeamColor(sub.Color);
        Color nameColor = idle ? Palette.Blend(teamColor, BgColor, 0.55f) : teamColor;
        Color textColor = idle ? Palette.Blend(FgColor, BgColor, 0.55f) : FgColor;
        var nameBrush = new SolidColorBrush(nameColor);
        var textBrush = new SolidColorBrush(textColor);

        DrawTeammateGlyph(ctx, glyphX, midY, nameColor);

        string stateText = idle ? "idle" : "working";
        double stateW = OverlayDraw.MeasureWidth(stateText, SubStatusSize);
        double labelX = glyphX + 16;
        double labelMaxW = width - labelX - HorizPad - stateW - 6;

        string handle = "@" + (string.IsNullOrWhiteSpace(sub.Name) ? "teammate" : sub.Name!.Trim());
        string handleTrunc = OverlayDraw.Truncate(handle, SubNameSize, labelMaxW);
        OverlayDraw.TextLeftMid(ctx, OverlayDraw.Text(handleTrunc, SubNameSize, nameBrush), labelX, midY);
        double x = labelX + OverlayDraw.MeasureWidth(handleTrunc, SubNameSize) + 8;

        string activity = idle ? "" : sub.Activity?.Trim() ?? "";
        if (activity.Length > 0)
        {
            double remaining = labelMaxW - (x - labelX);
            if (remaining > 24)
            {
                string actTrunc = OverlayDraw.Truncate(activity, SubNameSize, remaining);
                OverlayDraw.TextLeftMid(ctx, OverlayDraw.Text(actTrunc, SubNameSize, textBrush), x, midY);
            }
        }

        OverlayDraw.TextLeftMid(ctx, OverlayDraw.Text(stateText, SubStatusSize, idle ? textBrush : nameBrush),
            width - HorizPad - stateW, midY);
    }

    // A small "person" mark — a head circle above a shoulders dome — in the given colour, centred on (x, midY).
    private static void DrawTeammateGlyph(DrawingContext ctx, double x, double midY, Color color)
    {
        var brush = new SolidColorBrush(color);
        const double headD = 5;
        ctx.DrawEllipse(brush, null, new Point(x + headD / 2, midY - 5 + headD / 2), headD / 2, headD / 2);

        // Shoulders: the top half of a small ellipse below the head.
        var r = new Rect(x - 1, midY + 1, headD + 3, headD + 2);
        var dome = new StreamGeometry();
        using (var gc = dome.Open())
        {
            var leftPt = new Point(r.Left, r.Center.Y);
            var rightPt = new Point(r.Right, r.Center.Y);
            gc.BeginFigure(leftPt, isFilled: true);
            gc.ArcTo(rightPt, new Size(r.Width / 2, r.Height / 2), 0, false, SweepDirection.CounterClockwise);
            gc.EndFigure(true);
        }
        ctx.DrawGeometry(brush, null, dome);
    }

    // The background-session robot glyph: antenna + rounded-square face + two dot eyes.
    private static void DrawBotIcon(DrawingContext ctx, double x, double midY)
    {
        var pen = new Pen(BotBrush, 1.3, lineCap: PenLineCap.Round);
        const double w = 11, h = 9;
        double left = x, top = midY - h / 2 + 1;
        double cx = left + w / 2;

        ctx.DrawLine(pen, new Point(cx, top - 3), new Point(cx, top));          // antenna
        ctx.DrawEllipse(BotBrush, null, new Point(cx, top - 3.5), 1.5, 1.5);    // antenna cap
        ctx.DrawRectangle(null, pen, new RoundedRect(new Rect(left, top, w, h), 2)); // face
        ctx.DrawEllipse(BotBrush, null, new Point(left + 3.5, top + 4), 1, 1);  // eyes
        ctx.DrawEllipse(BotBrush, null, new Point(left + w - 3.5, top + 4), 1, 1);
    }

    // The remote-control "broadcast" glyph: a source dot with two quarter-arc waves rising up-right.
    private static void DrawRemoteIcon(DrawingContext ctx, double originX, double midY)
    {
        var pen = new Pen(RemoteBrush, 2.25, lineCap: PenLineCap.Round);
        double oy = midY + 4;
        ctx.DrawEllipse(RemoteBrush, null, new Point(originX, oy), 2, 2);
        OverlayDraw.Arc(ctx, pen, originX, oy, 5, 270, 90);
        OverlayDraw.Arc(ctx, pen, originX, oy, 9, 270, 90);
    }

    // The external-notify glyph: an envelope outline with a "V" flap.
    private static void DrawMailIcon(DrawingContext ctx, double x, double midY)
    {
        var pen = new Pen(MailBrush, 1.3, null, PenLineCap.Round, PenLineJoin.Round);
        const double w = 11, h = 8;
        double top = midY - h / 2;
        ctx.DrawRectangle(null, pen, new Rect(x, top, w, h));
        var flap = new StreamGeometry();
        using (var gc = flap.Open())
        {
            gc.BeginFigure(new Point(x, top), isFilled: false);
            gc.LineTo(new Point(x + w / 2, top + h / 2));
            gc.LineTo(new Point(x + w, top));
            gc.EndFigure(false);
        }
        ctx.DrawGeometry(null, pen, flap);
    }

    // The permission-mode badge: two fast-forward chevrons in the mode's colour, faded when idle.
    private static void DrawModeBadge(DrawingContext ctx, PermissionMode mode, double x, double midY, int alpha)
    {
        Color c = Palette.ModeColor(mode);
        if (alpha < 255) c = Color.FromArgb((byte)alpha, c.R, c.G, c.B);
        var brush = new SolidColorBrush(c);
        const double hh = 4, w = 5;
        Chevron(ctx, brush, x, midY, hh, w);
        Chevron(ctx, brush, x + w + 1, midY, hh, w);

        static void Chevron(DrawingContext ctx, IBrush brush, double x, double midY, double hh, double w)
        {
            var g = new StreamGeometry();
            using (var gc = g.Open())
            {
                gc.BeginFigure(new Point(x, midY - hh), isFilled: true);
                gc.LineTo(new Point(x + w, midY));
                gc.LineTo(new Point(x, midY + hh));
                gc.EndFigure(true);
            }
            ctx.DrawGeometry(brush, null, g);
        }
    }

    private static Bitmap? LoadBrand()
    {
        try { return new Bitmap(AssetLoader.Open(new Uri("avares://perch-avalonia/Assets/icon.png"))); }
        catch { return null; }
    }
}

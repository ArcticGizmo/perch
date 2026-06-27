using System.Drawing.Drawing2D;

using Perch.Data;
namespace Perch.Ui;

/// <summary>
/// The slim surface the overlay form gives <see cref="DenseModeController"/> so the controller can
/// own dense mode without owning the whole form. Geometry (location/size) and <c>Invalidate</c> come
/// straight off the <see cref="Control"/>; the rest are the few form internals the dense logic needs:
/// the shared full-panel size, the live session list and tray icon for the strip, and the handful of
/// side effects (relayout, tick-timer, hover/tooltip clearing) the dense transitions ripple back into
/// the form.
/// </summary>
internal interface IDenseHost
{
    Point     Location   { get; set; }
    Size      ClientSize { get; set; }
    int       Width      { get; }
    int       Height     { get; }
    Rectangle Bounds     { get; }
    void Invalidate();

    int FullPanelWidth { get; }                          // the floating / open-popup width
    int FullPanelHeight();                               // header + usage + quick-links + rows
    IReadOnlyList<ClaudeSession> Sessions { get; }
    Bitmap? Icon { get; }

    void RelayoutWindow();
    void UpdateTickTimer();
    void ClearRowHover();
    void HideUsageTooltip();
}

/// <summary>
/// Owns "dense mode": the alternate, out-of-the-way presentation where the overlay shrinks to a slim
/// strip hugging a screen edge and expands to the full panel on hover. It keeps the whole dense state
/// machine (whether dense is on, whether the hover popup is open, the remembered strip Y, which screen
/// edge/monitor it docks to, the floating position to restore on exit) plus the drag-to-redock drop
/// lanes and the closed-strip painting. The owning <see cref="OverlayForm"/> drives it through
/// <see cref="IDenseHost"/>; everything here used to live inline in that form.
/// </summary>
internal sealed class DenseModeController : IDisposable
{
    // Dense mode's first-time default sits well below the working-area top (4× the floating gap) so
    // the slim right-edge strip doesn't overlay application close icons.
    private const int DenseTopGapDefault = 64;

    // A narrow strip hugging a screen edge that expands on hover.
    private const int DenseClosedWidth = 44;
    private const int DenseTopPad      = 8;
    private const int DenseIconSize    = 22;
    private const int DenseGap         = 6;
    private const int DenseRowHeight   = 22;
    private const int DenseBottomPad   = 8;

    private readonly IDenseHost _host;

    // The four session-status dot colours, top-to-bottom display order, supplied by the host so the
    // strip's counts match the rest of the overlay exactly.
    private readonly Color _running, _awaiting, _attention, _idle;

    // _dense toggles the whole mode; _denseOpen is the hover-expanded popup within it. Floating and
    // dense each keep their own position: _floatingLoc holds the floating location while we're in
    // dense mode, and _denseY holds the dense strip's Y (its X is locked to the docked screen edge per
    // _denseSide). Nothing here is persisted across restarts.
    private bool  _dense;
    private bool  _denseOpen;
    private int   _denseY;
    private bool  _denseYInit;
    private Point _floatingLoc;

    // Which screen edge the dense strip hugs. Right matches the historical behaviour; dragging the
    // strip onto a left-edge drop lane flips it.
    private DenseSide _denseSide = DenseSide.Right;

    // Which monitor the dense strip is docked to, stored by device name so a stale Screen object can't
    // pin us to a monitor that's been disconnected. Null means the primary screen.
    private string? _denseScreenDevice;
    private readonly List<DenseDropZoneForm> _dropZones = [];
    private DenseDropZoneForm? _activeDropZone;

    // Collapses the hover-opened dense popup once the cursor has been away for 750ms. Re-validated
    // against the live cursor position so a quick out-and-back keeps it open.
    private readonly System.Windows.Forms.Timer _closeTimer;

    public DenseModeController(IDenseHost host, Color running, Color awaiting, Color attention, Color idle)
    {
        _host      = host;
        _running   = running;
        _awaiting  = awaiting;
        _attention = attention;
        _idle      = idle;

        _closeTimer = new System.Windows.Forms.Timer { Interval = 750 };
        _closeTimer.Tick += (_, _) =>
        {
            if (_dense && _denseOpen && !_host.Bounds.Contains(Cursor.Position))
                ClosePopup();
            else
                _closeTimer.Stop();
        };
    }

    // ── State exposed to the form ───────────────────────────────────────────────
    public bool      IsDense       => _dense;
    public bool      IsOpen        => _denseOpen;
    public bool      IsClosedStrip => _dense && !_denseOpen;   // strip visible, popup not open
    public DenseSide Side          => _denseSide;

    // Is the full session body currently on screen in dense mode (the hover-opened popup)?
    public bool ShowsFullPanel => _dense && _denseOpen;

    // ── Geometry ─────────────────────────────────────────────────────────────────
    // Sizes and positions the window for the current dense state: the closed strip or the open popup,
    // docked to the remembered edge at the remembered Y. Called from the form's RelayoutWindow.
    public void ApplyGeometry()
    {
        var wa = DenseScreen().WorkingArea;
        int w  = _denseOpen ? _host.FullPanelWidth  : DenseClosedWidth;
        int h  = _denseOpen ? _host.FullPanelHeight() : StripHeight();
        _host.Location   = new Point(DenseX(w, wa), ClampDenseY(_denseY, h, wa));
        _host.ClientSize = new Size(w, h);
    }

    // Height of the closed dense strip: the icon plus one row per non-zero status.
    private int StripHeight()
    {
        int visible = StatusCounts().Count(c => c.count > 0);
        int h = DenseTopPad + DenseIconSize;
        if (visible > 0)
            h += DenseGap + visible * DenseRowHeight;
        return h + DenseBottomPad;
    }

    // Keeps the dense strip fully on screen vertically as its height changes with the session count.
    private static int ClampDenseY(int y, int height, Rectangle wa) =>
        Math.Clamp(y, wa.Top, Math.Max(wa.Top, wa.Bottom - height));

    // X coordinate that hugs the docked edge: flush left, or flush right accounting for width.
    private int DenseX(int width, Rectangle wa) =>
        _denseSide == DenseSide.Left ? wa.Left : wa.Right - width;

    // Resolves the monitor the dense strip docks to. If the remembered monitor has been disconnected,
    // it self-heals by forgetting it and falling back to the primary screen.
    private Screen DenseScreen()
    {
        if (_denseScreenDevice != null)
        {
            foreach (var s in Screen.AllScreens)
                if (s.DeviceName == _denseScreenDevice)
                    return s;
            _denseScreenDevice = null;  // monitor vanished — reset to primary
        }
        return Screen.PrimaryScreen!;
    }

    // ── Transitions ──────────────────────────────────────────────────────────────
    public void Toggle()
    {
        if (_dense) Exit();
        else        Enter();
    }

    private void Enter()
    {
        if (_dense) return;
        _floatingLoc = _host.Location;                 // remember where floating lives
        if (!_denseYInit) { _denseY = DenseScreen().WorkingArea.Top + DenseTopGapDefault; _denseYInit = true; }
        _dense = true;
        _denseOpen = false;
        _host.ClearRowHover();
        _host.HideUsageTooltip();
        _host.RelayoutWindow();
        _host.UpdateTickTimer();
        _host.Invalidate();
    }

    private void Exit()
    {
        if (!_dense) return;
        _dense = false;
        _denseOpen = false;
        _closeTimer.Stop();
        _host.HideUsageTooltip();
        _host.Location = _floatingLoc;                 // restore the floating position
        _host.RelayoutWindow();
        _host.UpdateTickTimer();
        _host.Invalidate();
    }

    public void OpenPopup()
    {
        if (!_dense || _denseOpen) return;
        _denseOpen = true;
        _host.RelayoutWindow();
        _host.UpdateTickTimer();
        _host.Invalidate();
    }

    private void ClosePopup()
    {
        if (!_dense || !_denseOpen) return;
        _denseOpen = false;
        _closeTimer.Stop();
        _host.ClearRowHover();
        _host.HideUsageTooltip();
        _host.RelayoutWindow();
        _host.UpdateTickTimer();
        _host.Invalidate();
    }

    // ── Mouse / hover plumbing the form forwards ───────────────────────────────────
    // Hovering the strip pops the full panel open; any re-entry cancels a pending auto-close.
    public void OnMouseEntered()
    {
        _closeTimer.Stop();
        OpenPopup();
    }

    // Begin the countdown that collapses the open popup back to the strip. The form gates this on the
    // popup being open and no drag in progress.
    public void SchedulePopupClose() => _closeTimer.Start();

    // Re-evaluate docking when a monitor is added/removed (DenseScreen self-heals to primary).
    public void OnDisplaySettingsChanged()
    {
        if (_dense) { _host.RelayoutWindow(); _host.Invalidate(); }
    }

    // ── Drag to re-dock ────────────────────────────────────────────────────────────
    // While dragging the dense strip, every monitor gets a left- and a right-edge drop lane; releasing
    // over one re-pins the strip to that monitor and edge. The lane matching the current dock is
    // skipped (dropping there would be a no-op), so a single monitor still offers its opposite edge.
    public void ShowDropZones()
    {
        if (_dropZones.Count > 0) return;

        var current = DenseScreen();
        foreach (var s in Screen.AllScreens)
        {
            foreach (var side in (ReadOnlySpan<DenseSide>)[DenseSide.Left, DenseSide.Right])
            {
                if (s.DeviceName == current.DeviceName && side == _denseSide) continue;
                var zone = new DenseDropZoneForm(s, side);
                _dropZones.Add(zone);
                zone.Show();
            }
        }
    }

    // Dense stays hugging the current monitor's docked edge, moving only vertically; drop lanes let it
    // be re-pinned to another edge or monitor on release. <paramref name="targetTop"/> is the proposed
    // strip Y; <paramref name="cursorScreen"/> drives the active drop-lane highlight.
    public void DragVertical(int targetTop, Point cursorScreen)
    {
        var wa   = DenseScreen().WorkingArea;
        int newY = ClampDenseY(targetTop, _host.Height, wa);
        _host.Location = new Point(DenseX(_host.Width, wa), newY);
        _denseY  = newY;
        UpdateActiveDropZone(cursorScreen);
    }

    private void UpdateActiveDropZone(Point screenPt)
    {
        DenseDropZoneForm? hit = null;
        foreach (var z in _dropZones)
            if (z.ContainsScreenPoint(screenPt)) { hit = z; break; }

        if (hit == _activeDropZone) return;
        _activeDropZone?.SetActive(false);
        _activeDropZone = hit;
        _activeDropZone?.SetActive(true);
    }

    public void HideDropZones()
    {
        foreach (var z in _dropZones) z.Dispose();
        _dropZones.Clear();
        _activeDropZone = null;
    }

    // Re-pins the dense strip to the monitor and edge whose drop lane the cursor was released over,
    // dropping it at the release height (clamped onto that monitor). No-op if nothing was hovered.
    public void PinToActiveDropZone()
    {
        if (_activeDropZone == null) return;
        var screen = _activeDropZone.TargetScreen;
        _denseScreenDevice = screen.DeviceName;
        _denseSide = _activeDropZone.Side;
        _denseY = ClampDenseY(Cursor.Position.Y - _host.Height / 2, _host.Height, screen.WorkingArea);
        _host.RelayoutWindow();
        _host.Invalidate();
    }

    // ── Painting ─────────────────────────────────────────────────────────────────
    // The four statuses in top-to-bottom display order, paired with their dot colour.
    private (Color color, int count)[] StatusCounts() =>
    [
        (_running,   _host.Sessions.Count(s => s.Status == SessionStatus.Running)),
        (_awaiting,  _host.Sessions.Count(s => s.Status == SessionStatus.AwaitingInput)),
        (_attention, _host.Sessions.Count(s => s.Status == SessionStatus.NeedsAttention)),
        (_idle,      _host.Sessions.Count(s => s.Status == SessionStatus.Idle)),
    ];

    // The closed dense view: the perch icon, then one centered "dot + count" row for each
    // status that has at least one session. With no sessions at all, only the icon shows.
    public void PaintStrip(Graphics g)
    {
        int cx = _host.ClientSize.Width / 2;

        if (_host.Icon is { } icon)
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(icon, new Rectangle(cx - DenseIconSize / 2, DenseTopPad, DenseIconSize, DenseIconSize));
        }

        using var countFont = new Font("Segoe UI", 9f, FontStyle.Bold, GraphicsUnit.Point);
        const int Dot = 8;
        int y = DenseTopPad + DenseIconSize + DenseGap;

        foreach (var (color, count) in StatusCounts())
        {
            if (count == 0) continue;

            var label  = count.ToString();
            var sz     = g.MeasureString(label, countFont);
            int groupW = Dot + 4 + (int)sz.Width;
            int startX = cx - groupW / 2;
            int midY   = y + DenseRowHeight / 2;

            using (var brush = new SolidBrush(color))
            {
                g.FillEllipse(brush, startX, midY - Dot / 2, Dot, Dot);
                g.DrawString(label, countFont, brush, startX + Dot + 4, midY - sz.Height / 2);
            }
            y += DenseRowHeight;
        }
    }

    public void Dispose()
    {
        HideDropZones();
        _closeTimer.Dispose();
    }
}

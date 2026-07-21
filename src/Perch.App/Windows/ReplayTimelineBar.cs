using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Perch.Avalonia.Theming;
using Perch.Data.Replay;

namespace Perch.Avalonia.Windows;

/// <summary>
/// The replay scrub bar: an owner-drawn timeline over the whole scene that also plots every
/// <see cref="ReplayMarker"/> as a coloured tick (prompts, tool calls, sub-agent spawns, interrupts).
/// Click or drag anywhere to seek — a click near a marker snaps to it — and hovering a tick raises
/// <see cref="Hovered"/> so the window can show what it is. Replaces the plain slider so the "video
/// scrubber" reads at a glance which moments are interesting.
/// </summary>
internal sealed class ReplayTimelineBar : Control
{
    private const double Pad = 7;         // horizontal inset so end ticks/handle aren't clipped
    private const double TrackH = 5;
    private const double TickHalf = 9;    // tick extends this far above/below centre
    private const double SnapPx = 6;      // click/hover tolerance to a marker

    private long _duration = 1;
    private long _position;
    private IReadOnlyList<ReplayMarker> _markers = [];
    private int _hover = -1;
    private bool _dragging;

    /// <summary>Raised when the user seeks by clicking or dragging (position in scene ms).</summary>
    public event Action<long>? Seeked;

    /// <summary>Raised when the marker under the cursor changes (null when none).</summary>
    public event Action<ReplayMarker?>? Hovered;

    public ReplayTimelineBar()
    {
        Height = 36;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    public void SetDuration(long ms) { _duration = Math.Max(1, ms); InvalidateVisual(); }
    public void SetMarkers(IReadOnlyList<ReplayMarker> markers) { _markers = markers; InvalidateVisual(); }

    /// <summary>Moves the playhead without raising <see cref="Seeked"/> (for playback-driven updates).</summary>
    public void SetPosition(long ms)
    {
        _position = Math.Clamp(ms, 0, _duration);
        InvalidateVisual();
    }

    // ── Geometry ──────────────────────────────────────────────────────────────────
    private double TrackLeft => Pad;
    private double TrackW => Math.Max(1, Bounds.Width - 2 * Pad);
    private double CentreY => Bounds.Height / 2;
    private double XFor(long pos) => TrackLeft + TrackW * ((double)pos / _duration);
    private long PosFor(double x) => (long)Math.Clamp((x - TrackLeft) / TrackW * _duration, 0, _duration);

    // ── Input ─────────────────────────────────────────────────────────────────────
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _dragging = true;
        e.Pointer.Capture(this);
        var x = e.GetPosition(this).X;
        // A click near a tick snaps to that exact marker; otherwise seek to the clicked position.
        int near = NearestMarker(x);
        Seeked?.Invoke(near >= 0 ? _markers[near].ScenePos : PosFor(x));
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var x = e.GetPosition(this).X;
        if (_dragging)
        {
            Seeked?.Invoke(PosFor(x));
            return;
        }
        SetHover(NearestMarker(x));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragging) { _dragging = false; e.Pointer.Capture(null); }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        SetHover(-1);
    }

    private void SetHover(int index)
    {
        if (index == _hover) return;
        _hover = index;
        Hovered?.Invoke(index >= 0 ? _markers[index] : null);
        InvalidateVisual();
    }

    // The marker whose tick is within SnapPx of x, or -1. Ties break to the closest.
    private int NearestMarker(double x)
    {
        int best = -1;
        double bestD = SnapPx;
        for (int i = 0; i < _markers.Count; i++)
        {
            double d = Math.Abs(XFor(_markers[i].ScenePos) - x);
            if (d <= bestD) { bestD = d; best = i; }
        }
        return best;
    }

    // ── Render ────────────────────────────────────────────────────────────────────
    public override void Render(DrawingContext ctx)
    {
        double left = TrackLeft, top = CentreY - TrackH / 2;
        double playX = XFor(_position);

        // Track, then the played portion up to the playhead.
        var track = new RoundedRect(new Rect(left, top, TrackW, TrackH), TrackH / 2);
        ctx.DrawRectangle(new SolidColorBrush(Color.FromRgb(48, 48, 60)), null, track);
        using (ctx.PushClip(track))
            ctx.DrawRectangle(ReplayBlueBrush, null, new Rect(left, top, Math.Max(0, playX - left), TrackH));

        // Marker ticks, coloured by kind. Draw the hovered one last (on top) and taller.
        for (int i = 0; i < _markers.Count; i++)
        {
            if (i == _hover) continue;
            DrawTick(ctx, _markers[i], hovered: false);
        }
        if (_hover >= 0 && _hover < _markers.Count)
            DrawTick(ctx, _markers[_hover], hovered: true);

        // Playhead: a bright vertical line with a round handle on the track.
        var head = new SolidColorBrush(Color.FromRgb(240, 240, 248));
        ctx.DrawRectangle(head, null, new Rect(playX - 1, CentreY - TickHalf - 2, 2, (TickHalf + 2) * 2));
        ctx.DrawEllipse(head, null, new Point(playX, CentreY), 4.5, 4.5);
    }

    private void DrawTick(DrawingContext ctx, ReplayMarker m, bool hovered)
    {
        double x = XFor(m.ScenePos);
        var color = MarkerColor(m.Kind);
        double half = hovered ? TickHalf + 2 : TickHalf;
        double w = hovered ? 3 : 2;
        // A thin dark outline keeps a tick legible on any background — notably a light-blue "prompt" tick
        // sitting on the (also blue) played portion of the track.
        ctx.DrawRectangle(new SolidColorBrush(color), TickOutline, new Rect(x - w / 2, CentreY - half, w, half * 2));
        if (hovered)
            ctx.DrawEllipse(new SolidColorBrush(color), null, new Point(x, CentreY - half - 2), 3, 3);
    }

    private static readonly IPen TickOutline = new Pen(new SolidColorBrush(Color.FromArgb(170, 8, 8, 12)), 1);

    private static readonly IBrush ReplayBlueBrush = new SolidColorBrush(Color.FromRgb(56, 189, 248));

    // Distinct hues per marker kind so the timeline's shape reads at a glance.
    internal static Color MarkerColor(ReplayMarkerKind kind) => kind switch
    {
        ReplayMarkerKind.Prompt        => Color.FromRgb(56, 189, 248),   // light blue — a turn boundary
        ReplayMarkerKind.ToolUse       => Color.FromRgb(148, 163, 184),  // slate — a tool call
        ReplayMarkerKind.SubagentSpawn => Color.FromRgb(167, 139, 250),  // purple — a sub-agent spawn
        ReplayMarkerKind.Interrupt     => Color.FromRgb(248, 113, 113),  // red — an interrupt
        _                              => Palette.Muted,
    };
}

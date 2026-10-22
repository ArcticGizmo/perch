using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Perch.Data.Mermaid;

namespace Perch.Avalonia.Rendering;

/// <summary>
/// Paints a laid-out Mermaid diagram (<see cref="MermaidScene"/>) in the colours of the Markdown preview it sits in.
/// The layout runs once, up front, in <see cref="TryCreate"/>: it measures every label with the same
/// <see cref="FormattedText"/> this control later draws, so the boxes are sized from real line heights and the painted
/// text is exactly what was measured. Render only replays the scene: no layout, no shaping.
/// </summary>
internal sealed class MermaidDiagramView : Control
{
    private readonly MermaidScene _scene;
    private readonly Dictionary<(string, MermaidTextRole), FormattedText> _texts;

    private readonly IBrush _nodeFill, _clusterFill, _labelFill, _noteFill, _numberFill, _tagFill, _arrowFill, _messageFill;
    private readonly IBrush _erHeaderFill, _erRowFill;
    private readonly IPen _nodePen, _clusterPen, _notePen, _blockPen, _lifelinePen, _figurePen;
    private readonly IBrush _edgeBrush, _messageBrush;

    private MermaidDiagramView(MermaidScene scene, Dictionary<(string, MermaidTextRole), FormattedText> texts, MarkdownStyle s)
    {
        _scene = scene;
        _texts = texts;

        _nodeFill = s.CodeBg;
        _labelFill = s.CodeBg;
        _clusterFill = Tint(s.Muted, 0.07);
        _noteFill = Tint(s.Link, 0.13);
        _numberFill = s.Link;
        _tagFill = s.TableHeaderBg;
        _edgeBrush = s.Muted;
        _arrowFill = s.Muted;
        _messageBrush = Tint(s.Fg, 0.85);
        _messageFill = _messageBrush;
        _erHeaderFill = Tint(s.Link, 0.14);
        _erRowFill = Tint(s.Muted, 0.07);

        _nodePen = OverlayDraw.Pen(Tint(s.Link, 0.75), 1.3);
        _clusterPen = OverlayDraw.Pen(s.TableBorder, 1);
        _notePen = OverlayDraw.Pen(Tint(s.Link, 0.5), 1);
        _blockPen = OverlayDraw.Pen(Tint(s.Muted, 0.7), 1);
        _lifelinePen = OverlayDraw.Pen(Tint(s.Muted, 0.5), 1);
        _figurePen = OverlayDraw.Pen(s.Fg, 1.4, null, PenLineCap.Round, PenLineJoin.Round);
    }

    /// <summary>Lays <paramref name="diagram"/> out in <paramref name="style"/>'s type scale; null when it can't be
    /// (too large, or nothing to draw), so the caller shows the source instead.</summary>
    public static MermaidDiagramView? TryCreate(MermaidDiagram diagram, MarkdownStyle style)
    {
        var face = style.BodyFont ?? FontFamily.Default;
        var texts = new Dictionary<(string, MermaidTextRole), FormattedText>();
        MSize Measure(string text, MermaidTextRole role)
        {
            if (!texts.TryGetValue((text, role), out var ft))
                texts[(text, role)] = ft = Shape(text, role, style, face);
            return new MSize(Math.Ceiling(ft.WidthIncludingTrailingWhitespace), Math.Ceiling(ft.Height));
        }
        try
        {
            var scene = MermaidLayout.Layout(diagram, Measure);
            return scene is { Width: > 0, Height: > 0 } ? new MermaidDiagramView(scene, texts, style) : null;
        }
        catch
        {
            return null;
        }
    }

    // A label's size, weight, colour and wrap width, by what it is.
    private static FormattedText Shape(string text, MermaidTextRole role, MarkdownStyle s, FontFamily face)
    {
        double b = s.BodySize;
        var (size, weight, brush, wrap) = role switch
        {
            MermaidTextRole.Node => (b * 0.95, FontWeight.Normal, s.Fg, 200.0),
            MermaidTextRole.EdgeLabel => (b * 0.86, FontWeight.Normal, s.Fg, 160.0),
            MermaidTextRole.ClusterTitle => (b * 0.88, FontWeight.SemiBold, s.Muted, 0.0),
            MermaidTextRole.Actor => (b * 0.95, FontWeight.Normal, s.Fg, 200.0),
            MermaidTextRole.Message => (b * 0.88, FontWeight.Normal, s.Fg, 280.0),
            MermaidTextRole.Note => (b * 0.86, FontWeight.Normal, s.Fg, 220.0),
            MermaidTextRole.BlockTag => (b * 0.8, FontWeight.Bold, s.Fg, 0.0),
            MermaidTextRole.BlockLabel => (b * 0.84, FontWeight.Normal, s.Muted, 0.0),
            MermaidTextRole.ErEntity => (b * 0.92, FontWeight.SemiBold, s.Fg, 0.0),
            MermaidTextRole.ErAttribute => (b * 0.84, FontWeight.Normal, s.Fg, 0.0),
            MermaidTextRole.ErAttributeMuted => (b * 0.84, FontWeight.Normal, s.Muted, 0.0),
            MermaidTextRole.ErKey => (b * 0.76, FontWeight.Bold, s.Link, 0.0),
            _ => (b * 0.72, FontWeight.Bold, s.CodeBg, 0.0),   // Number: paper-coloured on the accent disc
        };
        var ft = new FormattedText(text, CultureInfo.CurrentCulture, global::Avalonia.Media.FlowDirection.LeftToRight,
            new Typeface(face, FontStyle.Normal, weight), size, brush)
        {
            TextAlignment = TextAlignment.Center,
        };
        if (wrap > 0)
        {
            // Wrap at the role's width, then shrink the box to the widest line so centred lines centre on the label
            // itself, not on the wrap width.
            ft.MaxTextWidth = wrap;
            ft.MaxTextWidth = Math.Ceiling(ft.WidthIncludingTrailingWhitespace) + 1;
        }
        else ft.MaxTextWidth = Math.Ceiling(ft.WidthIncludingTrailingWhitespace) + 1;
        return ft;
    }

    protected override Size MeasureOverride(Size availableSize) => new(_scene.Width, _scene.Height);

    public override void Render(DrawingContext ctx)
    {
        foreach (var item in _scene.Items)
            switch (item)
            {
                case SceneShape s: DrawShape(ctx, s); break;
                case SceneLine l: DrawLine(ctx, l); break;
                case SceneText t when _texts.TryGetValue((t.Text, t.Role), out var ft):
                    ctx.DrawText(ft, new Point(t.Box.X, t.Box.Y));
                    break;
            }
    }

    // ── Shapes ──

    private void DrawShape(DrawingContext ctx, SceneShape s)
    {
        var r = ToRect(s.Box);
        switch (s.Role)
        {
            case MermaidShapeRole.Cluster:
                ctx.DrawRectangle(_clusterFill, _clusterPen, new RoundedRect(r, 6));
                return;
            case MermaidShapeRole.LabelBackground:
                ctx.DrawRectangle(_labelFill, null, new RoundedRect(r, 3));
                return;
            case MermaidShapeRole.Note:
                ctx.DrawRectangle(_nodeFill, null, r);          // opaque first, so the tint doesn't show lifelines through
                ctx.DrawRectangle(_noteFill, _notePen, r);
                return;
            case MermaidShapeRole.Activation:
                ctx.DrawRectangle(_nodeFill, _blockPen, r);
                return;
            case MermaidShapeRole.Block:
                ctx.DrawRectangle(null, _blockPen, r);
                return;
            case MermaidShapeRole.BlockTag:
                ctx.DrawGeometry(_tagFill, _blockPen, TagGeometry(r));
                return;
            case MermaidShapeRole.Number:
                ctx.DrawEllipse(_numberFill, null, r.Center, r.Width / 2, r.Height / 2);
                return;
            case MermaidShapeRole.ErHeader:
                ctx.DrawRectangle(_erHeaderFill, null, r);
                return;
            case MermaidShapeRole.ErRow:
                ctx.DrawRectangle(_erRowFill, null, r);
                return;
            case MermaidShapeRole.Actor when s.Stick:
                StickFigure(ctx, r);
                return;
        }
        DrawNode(ctx, s.Shape, r);
    }

    private void DrawNode(DrawingContext ctx, FlowShape shape, Rect r)
    {
        switch (shape)
        {
            case FlowShape.Round:
                ctx.DrawRectangle(_nodeFill, _nodePen, new RoundedRect(r, 8));
                break;
            case FlowShape.Stadium:
                ctx.DrawRectangle(_nodeFill, _nodePen, new RoundedRect(r, r.Height / 2));
                break;
            case FlowShape.Subroutine:
                ctx.DrawRectangle(_nodeFill, _nodePen, r);
                ctx.DrawLine(_nodePen, new Point(r.X + 8, r.Y), new Point(r.X + 8, r.Bottom));
                ctx.DrawLine(_nodePen, new Point(r.Right - 8, r.Y), new Point(r.Right - 8, r.Bottom));
                break;
            case FlowShape.Circle:
                ctx.DrawEllipse(_nodeFill, _nodePen, r.Center, r.Width / 2, r.Height / 2);
                break;
            case FlowShape.DoubleCircle:
                ctx.DrawEllipse(_nodeFill, _nodePen, r.Center, r.Width / 2, r.Height / 2);
                ctx.DrawEllipse(null, _nodePen, r.Center, r.Width / 2 - 4, r.Height / 2 - 4);
                break;
            case FlowShape.Cylinder:
                Cylinder(ctx, r);
                break;
            default:
                if (MermaidGeometry.Polygon(shape, ToMRect(r)) is { } poly)
                    ctx.DrawGeometry(_nodeFill, _nodePen, PolygonOf(poly));
                else
                    ctx.DrawRectangle(_nodeFill, _nodePen, new RoundedRect(r, 3));
                break;
        }
    }

    private void Cylinder(DrawingContext ctx, Rect r)
    {
        double ry = MermaidGeometry.CylinderCap(r.Width), rx = r.Width / 2;
        var body = new StreamGeometry();
        using (var g = body.Open())
        {
            g.BeginFigure(new Point(r.X, r.Y + ry), true);
            g.ArcTo(new Point(r.Right, r.Y + ry), new Size(rx, ry), 0, false, SweepDirection.Clockwise);
            g.LineTo(new Point(r.Right, r.Bottom - ry));
            g.ArcTo(new Point(r.X, r.Bottom - ry), new Size(rx, ry), 0, false, SweepDirection.Clockwise);
            g.EndFigure(true);
        }
        ctx.DrawGeometry(_nodeFill, _nodePen, body);
        // The near rim of the top cap, so the top reads as an opening.
        var rim = new StreamGeometry();
        using (var g = rim.Open())
        {
            g.BeginFigure(new Point(r.X, r.Y + ry), false);
            g.ArcTo(new Point(r.Right, r.Y + ry), new Size(rx, ry), 0, false, SweepDirection.CounterClockwise);
            g.EndFigure(false);
        }
        ctx.DrawGeometry(null, _nodePen, rim);
    }

    // A block's corner tag: a box with its bottom-right corner clipped, the way Mermaid labels a loop or alt.
    private static Geometry TagGeometry(Rect r)
    {
        double cut = Math.Min(6, r.Height / 3);
        return Polygon([
            new(r.X, r.Y), new(r.Right, r.Y), new(r.Right, r.Bottom - cut), new(r.Right - cut, r.Bottom), new(r.X, r.Bottom),
        ]);
    }

    private void StickFigure(DrawingContext ctx, Rect r)
    {
        double cx = r.Center.X, head = Math.Min(5.5, r.Height * 0.17);
        double neck = r.Y + 2 * head + 1, hip = r.Y + r.Height * 0.66;
        ctx.DrawEllipse(_nodeFill, _figurePen, new Point(cx, r.Y + head + 0.5), head, head);
        ctx.DrawLine(_figurePen, new Point(cx, neck), new Point(cx, hip));
        ctx.DrawLine(_figurePen, new Point(r.X + 2, neck + (hip - neck) * 0.3), new Point(r.Right - 2, neck + (hip - neck) * 0.3));
        ctx.DrawLine(_figurePen, new Point(cx, hip), new Point(r.X + 3, r.Bottom));
        ctx.DrawLine(_figurePen, new Point(cx, hip), new Point(r.Right - 3, r.Bottom));
    }

    // ── Lines ──

    private void DrawLine(DrawingContext ctx, SceneLine l)
    {
        if (l.Points.Count < 2 || l.Stroke == MermaidStroke.Invisible) return;
        bool message = l.Role == MermaidLineRole.Message;
        var brush = message ? _messageBrush : _edgeBrush;

        if (l.Role == MermaidLineRole.Lifeline)
        {
            ctx.DrawLine(_lifelinePen, ToPoint(l.Points[0]), ToPoint(l.Points[^1]));
            return;
        }

        double width = l.Stroke == MermaidStroke.Thick ? 2.6 : l.Role == MermaidLineRole.Divider ? 1 : 1.3;
        IDashStyle? dash = l.Stroke == MermaidStroke.Dotted ? new ImmutableDashStyle([3, 3], 0) : null;
        var pen = l.Role == MermaidLineRole.Divider
            ? OverlayDraw.Pen(Tint(_edgeBrush, 0.7), 1, dash)
            : OverlayDraw.Pen(brush, width, dash, PenLineCap.Flat, PenLineJoin.Round);

        var pts = l.Points.Select(ToPoint).ToList();
        double headLen = 8 + width;
        // Stop the stroke short of a head so a thick line doesn't poke through its tip.
        if (l.EndHead != MermaidHead.None) pts[^1] = Back(pts[^1], pts[^2], Inset(l.EndHead, headLen));
        if (l.StartHead != MermaidHead.None) pts[0] = Back(pts[0], pts[1], Inset(l.StartHead, headLen));

        ctx.DrawGeometry(null, pen, l.Curved && pts.Count > 2 ? Smooth(pts) : Polyline(pts));

        var fill = message ? _messageFill : _arrowFill;
        if (l.EndHead != MermaidHead.None) Head(ctx, l.EndHead, ToPoint(l.Points[^1]), ToPoint(l.Points[^2]), headLen, fill, brush);
        if (l.StartHead != MermaidHead.None) Head(ctx, l.StartHead, ToPoint(l.Points[0]), ToPoint(l.Points[1]), headLen, fill, brush);
    }

    private static double Inset(MermaidHead head, double len) => head switch
    {
        MermaidHead.Arrow => len * 0.8,
        MermaidHead.Circle => 8,
        _ => 0,
    };

    // A head at `tip`, pointing away from `from`.
    private void Head(DrawingContext ctx, MermaidHead head, Point tip, Point from, double len, IBrush fill, IBrush stroke)
    {
        var (ux, uy) = Unit(from, tip);
        double nx = -uy, ny = ux;
        switch (head)
        {
            case MermaidHead.Arrow:
            {
                double half = len * 0.45;
                var b = new Point(tip.X - ux * len, tip.Y - uy * len);
                ctx.DrawGeometry(fill, null, Polygon([
                    new(tip.X, tip.Y), new(b.X + nx * half, b.Y + ny * half), new(b.X - nx * half, b.Y - ny * half),
                ]));
                break;
            }
            case MermaidHead.Open:
            {
                double half = len * 0.45;
                var b = new Point(tip.X - ux * len, tip.Y - uy * len);
                var pen = OverlayDraw.Pen(stroke, 1.3, null, PenLineCap.Round, PenLineJoin.Round);
                ctx.DrawLine(pen, tip, new Point(b.X + nx * half, b.Y + ny * half));
                break;
            }
            case MermaidHead.Circle:
                ctx.DrawEllipse(_nodeFill, OverlayDraw.Pen(stroke, 1.3),
                    new Point(tip.X - ux * 4.5, tip.Y - uy * 4.5), 4, 4);
                break;
            case MermaidHead.Cross:
            {
                var c = new Point(tip.X - ux * 5, tip.Y - uy * 5);
                var pen = OverlayDraw.Pen(stroke, 1.6, null, PenLineCap.Round);
                double k = 3.8;
                ctx.DrawLine(pen, new Point(c.X - (ux + nx) * k, c.Y - (uy + ny) * k), new Point(c.X + (ux + nx) * k, c.Y + (uy + ny) * k));
                ctx.DrawLine(pen, new Point(c.X - (ux - nx) * k, c.Y - (uy - ny) * k), new Point(c.X + (ux - nx) * k, c.Y + (uy - ny) * k));
                break;
            }
            case MermaidHead.ErExactlyOne or MermaidHead.ErZeroOrOne or MermaidHead.ErOneOrMore or MermaidHead.ErZeroOrMore:
                Cardinality(ctx, head, tip, ux, uy, OverlayDraw.Pen(stroke, 1.3, null, PenLineCap.Round));
                break;
        }
    }

    // A crow's-foot marker at an entity: a bar for "one", a ring for "zero", a three-pronged foot for "many", read
    // outward from the entity (so "zero or more" is foot then ring, as in Mermaid). Solid even on a dashed line.
    private void Cardinality(DrawingContext ctx, MermaidHead head, Point tip, double ux, double uy, IPen pen)
    {
        double nx = -uy, ny = ux;
        const double half = 6;
        Point At(double back) => new(tip.X - ux * back, tip.Y - uy * back);
        void Bar(double back)
        {
            var c = At(back);
            ctx.DrawLine(pen, new Point(c.X + nx * half, c.Y + ny * half), new Point(c.X - nx * half, c.Y - ny * half));
        }
        void Ring(double back) => ctx.DrawEllipse(_nodeFill, pen, At(back), 4, 4);
        void Foot()
        {
            var root = At(12);
            ctx.DrawLine(pen, root, new Point(tip.X + nx * half, tip.Y + ny * half));
            ctx.DrawLine(pen, root, new Point(tip.X - nx * half, tip.Y - ny * half));
        }

        switch (head)
        {
            case MermaidHead.ErExactlyOne: Bar(6); Bar(11); break;
            case MermaidHead.ErZeroOrOne: Bar(6); Ring(15); break;
            case MermaidHead.ErOneOrMore: Foot(); Bar(16); break;
            default: Foot(); Ring(17); break;   // zero or more
        }
    }

    // A Catmull-Rom spline through the points, as cubic Béziers, so a routed edge bends smoothly through its dummies.
    private static Geometry Smooth(List<Point> p)
    {
        var g = new StreamGeometry();
        using var c = g.Open();
        c.BeginFigure(p[0], false);
        for (int i = 0; i + 1 < p.Count; i++)
        {
            var p0 = p[Math.Max(0, i - 1)];
            var p1 = p[i];
            var p2 = p[i + 1];
            var p3 = p[Math.Min(p.Count - 1, i + 2)];
            c.CubicBezierTo(
                new Point(p1.X + (p2.X - p0.X) / 6, p1.Y + (p2.Y - p0.Y) / 6),
                new Point(p2.X - (p3.X - p1.X) / 6, p2.Y - (p3.Y - p1.Y) / 6),
                p2);
        }
        c.EndFigure(false);
        return g;
    }

    private static Geometry Polyline(List<Point> p)
    {
        var g = new StreamGeometry();
        using var c = g.Open();
        c.BeginFigure(p[0], false);
        for (int i = 1; i < p.Count; i++) c.LineTo(p[i]);
        c.EndFigure(false);
        return g;
    }

    private static Geometry PolygonOf(IReadOnlyList<MPoint> p) => Polygon(p.Select(ToPoint).ToList());

    private static Geometry Polygon(List<Point> p)
    {
        var g = new StreamGeometry();
        using var c = g.Open();
        c.BeginFigure(p[0], true);
        for (int i = 1; i < p.Count; i++) c.LineTo(p[i]);
        c.EndFigure(true);
        return g;
    }

    // ── Helpers ──

    private static (double X, double Y) Unit(Point from, Point to)
    {
        double dx = to.X - from.X, dy = to.Y - from.Y, len = Math.Sqrt(dx * dx + dy * dy);
        return len < 1e-9 ? (0, 1) : (dx / len, dy / len);
    }

    private static Point Back(Point tip, Point from, double by)
    {
        var (ux, uy) = Unit(from, tip);
        return new Point(tip.X - ux * by, tip.Y - uy * by);
    }

    private static IBrush Tint(IBrush brush, double alpha) =>
        brush is ISolidColorBrush s ? new ImmutableSolidColorBrush(s.Color, s.Opacity * alpha) : brush;

    private static Point ToPoint(MPoint p) => new(p.X, p.Y);
    private static Rect ToRect(MRect r) => new(r.X, r.Y, Math.Max(0, r.W), Math.Max(0, r.H));
    private static MRect ToMRect(Rect r) => new(r.X, r.Y, r.Width, r.Height);
}

namespace Perch.Data.Mermaid;

/// <summary>Flowchart shape geometry, shared by the layout (sizing a node around its label, clipping edges at its
/// outline) and the painter (drawing that same outline), so an arrow always lands on the edge it's drawn to.</summary>
internal static class MermaidGeometry
{
    private const double PadX = 14, PadY = 9;

    /// <summary>The bounding box a shape needs to hold a label of size <paramref name="t"/>.</summary>
    public static MSize ShapeSize(FlowShape shape, MSize t)
    {
        double w = t.W + 2 * PadX, h = t.H + 2 * PadY;
        switch (shape)
        {
            case FlowShape.Subroutine: return new(w + 16, h);
            case FlowShape.Stadium: return new(w + h / 2, h);
            case FlowShape.Cylinder: return new(w, h + 2 * CylinderCap(w));
            case FlowShape.Circle:
            {
                double d = Math.Max(Math.Sqrt(t.W * t.W + t.H * t.H) + 14, 36);
                return new(d, d);
            }
            case FlowShape.DoubleCircle:
            {
                double d = Math.Max(Math.Sqrt(t.W * t.W + t.H * t.H) + 24, 44);
                return new(d, d);
            }
            case FlowShape.Asymmetric: return new(w + h / 2, h);
            case FlowShape.Rhombus:
            {
                // A diamond with half-diagonals a, b holds a w×h box when w/2a + h/2b ≤ 1. Fix the height a little
                // above the text (so a long label makes a wide, flat diamond, not a towering one) and solve the width.
                double iw = t.W + 10, ih = t.H + 6;
                double H = ih + Math.Max(30, iw * 0.35);
                double W = iw / (1 - ih / H);
                return new(W, H);
            }
            case FlowShape.Hexagon: return new(w + h / 2, h);
            case FlowShape.Parallelogram or FlowShape.ParallelogramAlt or FlowShape.Trapezoid or FlowShape.TrapezoidAlt:
                return new(w + 2 * Slant(h), h);
            default: return new(w, h);
        }
    }

    /// <summary>The vertical radius of a cylinder's elliptical caps.</summary>
    public static double CylinderCap(double width) => Math.Clamp(width * 0.07, 4, 9);

    private static double Slant(double h) => h * 0.35;

    /// <summary>The outline of a polygonal shape (rhombus, hexagon, parallelograms, trapezoids, asymmetric) filling
    /// <paramref name="b"/>, clockwise from the top-left; null for the curved and rectangular shapes.</summary>
    public static MPoint[]? Polygon(FlowShape shape, MRect b)
    {
        double s = Slant(b.H);
        return shape switch
        {
            FlowShape.Rhombus =>
                [new(b.CenterX, b.Y), new(b.Right, b.CenterY), new(b.CenterX, b.Bottom), new(b.X, b.CenterY)],
            FlowShape.Hexagon =>
                [new(b.X + b.H / 4, b.Y), new(b.Right - b.H / 4, b.Y), new(b.Right, b.CenterY),
                 new(b.Right - b.H / 4, b.Bottom), new(b.X + b.H / 4, b.Bottom), new(b.X, b.CenterY)],
            FlowShape.Parallelogram =>
                [new(b.X + 2 * s, b.Y), new(b.Right, b.Y), new(b.Right - 2 * s, b.Bottom), new(b.X, b.Bottom)],
            FlowShape.ParallelogramAlt =>
                [new(b.X, b.Y), new(b.Right - 2 * s, b.Y), new(b.Right, b.Bottom), new(b.X + 2 * s, b.Bottom)],
            FlowShape.Trapezoid =>
                [new(b.X + 2 * s, b.Y), new(b.Right - 2 * s, b.Y), new(b.Right, b.Bottom), new(b.X, b.Bottom)],
            FlowShape.TrapezoidAlt =>
                [new(b.X, b.Y), new(b.Right, b.Y), new(b.Right - 2 * s, b.Bottom), new(b.X + 2 * s, b.Bottom)],
            FlowShape.Asymmetric =>
                [new(b.X, b.Y), new(b.Right, b.Y), new(b.Right, b.Bottom), new(b.X, b.Bottom), new(b.X + b.H / 2, b.CenterY)],
            _ => null,
        };
    }

    /// <summary>Where the ray from the shape's centre toward <paramref name="toward"/> crosses its outline.</summary>
    public static MPoint Clip(FlowShape shape, MRect b, MPoint toward)
    {
        var c = b.Center;
        double dx = toward.X - c.X, dy = toward.Y - c.Y;
        if (Math.Abs(dx) < 1e-9 && Math.Abs(dy) < 1e-9) return c;

        if (shape is FlowShape.Circle or FlowShape.DoubleCircle)
        {
            double a = b.W / 2, e = b.H / 2;
            double t = 1 / Math.Sqrt(dx * dx / (a * a) + dy * dy / (e * e));
            return new(c.X + dx * t, c.Y + dy * t);
        }

        if (Polygon(shape, b) is { } poly)
        {
            double best = double.MaxValue;
            for (int i = 0; i < poly.Length; i++)
            {
                var p = poly[i];
                var q = poly[(i + 1) % poly.Length];
                if (RaySegment(c, dx, dy, p, q) is { } t && t < best) best = t;
            }
            if (best < double.MaxValue) return new(c.X + dx * best, c.Y + dy * best);
        }

        double tr = Math.Min(Math.Abs(dx) < 1e-9 ? double.MaxValue : b.W / 2 / Math.Abs(dx),
                             Math.Abs(dy) < 1e-9 ? double.MaxValue : b.H / 2 / Math.Abs(dy));
        return new(c.X + dx * tr, c.Y + dy * tr);
    }

    // The ray parameter t ≥ 0 at which origin + t·(dx, dy) meets segment pq, or null if it misses.
    private static double? RaySegment(MPoint o, double dx, double dy, MPoint p, MPoint q)
    {
        double ex = q.X - p.X, ey = q.Y - p.Y;
        double den = dx * ey - dy * ex;
        if (Math.Abs(den) < 1e-12) return null;
        double t = ((p.X - o.X) * ey - (p.Y - o.Y) * ex) / den;
        double u = ((p.X - o.X) * dy - (p.Y - o.Y) * dx) / den;
        return t >= 0 && u >= -1e-9 && u <= 1 + 1e-9 ? t : null;
    }

    /// <summary>The first point of segment <paramref name="from"/>→<paramref name="to"/> that lies on or inside
    /// <paramref name="r"/>, or null when the segment never reaches it.</summary>
    public static MPoint? EnterRect(MPoint from, MPoint to, MRect r)
    {
        double t0 = 0, t1 = 1, dx = to.X - from.X, dy = to.Y - from.Y;
        bool Clip(double p, double q)
        {
            if (Math.Abs(p) < 1e-12) return q >= 0;
            double t = q / p;
            if (p < 0) { if (t > t1) return false; if (t > t0) t0 = t; }
            else { if (t < t0) return false; if (t < t1) t1 = t; }
            return true;
        }
        if (Clip(-dx, from.X - r.X) && Clip(dx, r.Right - from.X) && Clip(-dy, from.Y - r.Y) && Clip(dy, r.Bottom - from.Y))
            return new(from.X + dx * t0, from.Y + dy * t0);
        return null;
    }
}

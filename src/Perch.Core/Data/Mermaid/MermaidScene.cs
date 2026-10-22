namespace Perch.Data.Mermaid;

internal readonly record struct MPoint(double X, double Y);

internal readonly record struct MSize(double W, double H);

internal readonly record struct MRect(double X, double Y, double W, double H)
{
    public double Right => X + W;
    public double Bottom => Y + H;
    public double CenterX => X + W / 2;
    public double CenterY => Y + H / 2;
    public MPoint Center => new(CenterX, CenterY);

    public static MRect Centered(MPoint c, MSize s) => new(c.X - s.W / 2, c.Y - s.H / 2, s.W, s.H);

    public MRect Inflate(double l, double t, double r, double b) => new(X - l, Y - t, W + l + r, H + t + b);

    public MRect Union(MRect o)
    {
        double x = Math.Min(X, o.X), y = Math.Min(Y, o.Y);
        return new MRect(x, y, Math.Max(Right, o.Right) - x, Math.Max(Bottom, o.Bottom) - y);
    }

    public bool Contains(MPoint p) => p.X >= X && p.X <= Right && p.Y >= Y && p.Y <= Bottom;
}

/// <summary>What a piece of text is, which fixes its font size, weight and wrap width. The layout measures text
/// through a callback keyed on this, and the painter draws it with the same settings, so the two always agree.</summary>
internal enum MermaidTextRole
{
    Node, EdgeLabel, ClusterTitle, Actor, Message, Note, BlockTag, BlockLabel, Number,
    ErEntity, ErAttribute, ErAttributeMuted, ErKey,
}

/// <summary>What a shape is, which fixes its colours in the painter.</summary>
internal enum MermaidShapeRole { Node, Cluster, Actor, Note, Activation, Block, BlockTag, LabelBackground, Number, ErHeader, ErRow }

internal enum MermaidLineRole { Edge, Lifeline, Message, Divider }

internal abstract record MermaidSceneItem;

/// <summary>A filled, outlined shape. <see cref="Box"/> is its bounding box; the shape fills it.</summary>
internal sealed record SceneShape(FlowShape Shape, MRect Box, MermaidShapeRole Role, bool Stick = false) : MermaidSceneItem;

/// <summary>A connector through <see cref="Points"/>. <see cref="Curved"/> asks the painter to smooth a path of more
/// than two points; heads sit at the first/last point, pointing along the end segments.</summary>
internal sealed record SceneLine(
    IReadOnlyList<MPoint> Points, MermaidStroke Stroke, MermaidHead StartHead, MermaidHead EndHead, bool Curved,
    MermaidLineRole Role) : MermaidSceneItem;

/// <summary>Text centred in <see cref="Box"/> (left-aligned when <see cref="AlignLeft"/>), as the role measured it.</summary>
internal sealed record SceneText(string Text, MRect Box, MermaidTextRole Role, bool AlignLeft = false) : MermaidSceneItem;

/// <summary>A laid-out diagram: a canvas size and the items to paint, in paint order (later items on top).</summary>
internal sealed class MermaidScene(double width, double height, IReadOnlyList<MermaidSceneItem> items)
{
    public double Width { get; } = width;
    public double Height { get; } = height;
    public IReadOnlyList<MermaidSceneItem> Items { get; } = items;
}

/// <summary>Lays a parsed diagram out into a <see cref="MermaidScene"/>.</summary>
internal static class MermaidLayout
{
    /// <summary>Lay out <paramref name="diagram"/>, measuring each piece of text through <paramref name="measure"/>.
    /// Returns null when the diagram is too big to lay out sensibly (the caller shows its source instead).</summary>
    public static MermaidScene? Layout(MermaidDiagram diagram, Func<string, MermaidTextRole, MSize> measure) => diagram switch
    {
        MermaidFlowchart f => FlowchartLayout.Layout(f, measure),
        MermaidSequence s => SequenceLayout.Layout(s, measure),
        MermaidEr e => ErLayout.Layout(e, measure),
        _ => null,
    };

    // Translate every item so the scene's content starts at (margin, margin), and size the canvas to fit it (and
    // `extent`, room the caller wants kept even where nothing is painted).
    internal static MermaidScene Normalize(List<MermaidSceneItem> items, double margin, MRect? extent = null)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        void Take(MRect r)
        {
            minX = Math.Min(minX, r.X); minY = Math.Min(minY, r.Y);
            maxX = Math.Max(maxX, r.Right); maxY = Math.Max(maxY, r.Bottom);
        }
        if (extent is { } e) Take(e);
        foreach (var it in items)
            switch (it)
            {
                case SceneShape s: Take(s.Box); break;
                case SceneText t: Take(t.Box); break;
                case SceneLine l: foreach (var p in l.Points) Take(new MRect(p.X, p.Y, 0, 0)); break;
            }
        if (minX == double.MaxValue) return new MermaidScene(0, 0, items);

        double dx = margin - minX, dy = margin - minY;
        MRect Move(MRect r) => r with { X = r.X + dx, Y = r.Y + dy };
        var moved = items.Select<MermaidSceneItem, MermaidSceneItem>(it => it switch
        {
            SceneShape s => s with { Box = Move(s.Box) },
            SceneText t => t with { Box = Move(t.Box) },
            SceneLine l => l with { Points = l.Points.Select(p => new MPoint(p.X + dx, p.Y + dy)).ToList() },
            _ => it,
        }).ToList();
        return new MermaidScene(maxX - minX + 2 * margin, maxY - minY + 2 * margin, moved);
    }
}

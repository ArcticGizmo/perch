namespace Perch.Data.Mermaid;

/// <summary>
/// Lays an ER diagram out on the shared <see cref="LayeredLayout"/>. Each entity is a box: its name in a header band,
/// then one row per attribute in aligned columns (type, name, keys, comment — a column only when some row uses it).
/// Each relationship is an edge, solid when identifying and dashed when not, with a crow's-foot cardinality marker at
/// each end and its label on a pill midway.
/// </summary>
internal static class ErLayout
{
    private const double Margin = 6, HeaderPadX = 14, HeaderPadY = 8, CellPadX = 8, RowPadY = 3, MinBoxW = 110;
    // Long and wide enough that the markers at the loop's two ends don't run into each other.
    private const double LoopReach = 44, LoopSpread = 16;
    private const int MaxEntities = 300;
    private static readonly LayeredSpacing Spacing = new(40, 40);

    private sealed record Column(Func<ErAttribute, string> Text, MermaidTextRole Role, double Width);

    public static MermaidScene? Layout(MermaidEr er, Func<string, MermaidTextRole, MSize> measure)
    {
        if (er.Entities.Count == 0 || er.Entities.Count > MaxEntities) return null;
        bool vertical = er.Direction is FlowDirection.TopDown or FlowDirection.BottomUp;
        var index = new Dictionary<string, int>();
        for (int i = 0; i < er.Entities.Count; i++) index[er.Entities[i].Id] = i;

        // ── Entity boxes ──
        var columns = new List<Column>[er.Entities.Count];
        var rowH = new double[er.Entities.Count];
        var headerH = new double[er.Entities.Count];
        var sizes = new MSize[er.Entities.Count];
        for (int i = 0; i < er.Entities.Count; i++)
        {
            var e = er.Entities[i];
            var title = measure(e.Label, MermaidTextRole.ErEntity);
            headerH[i] = title.H + 2 * HeaderPadY;
            var cols = new List<Column>();
            foreach (var (text, role) in new (Func<ErAttribute, string>, MermaidTextRole)[]
                     {
                         (a => a.Type, MermaidTextRole.ErAttributeMuted), (a => a.Name, MermaidTextRole.ErAttribute),
                         (a => a.Keys, MermaidTextRole.ErKey), (a => a.Comment, MermaidTextRole.ErAttributeMuted),
                     })
            {
                var used = e.Attributes.Select(text).Where(s => s.Length > 0).ToList();
                if (used.Count == 0) continue;
                cols.Add(new Column(text, role, used.Max(s => measure(s, role).W) + 2 * CellPadX));
                rowH[i] = Math.Max(rowH[i], used.Max(s => measure(s, role).H) + 2 * RowPadY);
            }
            columns[i] = cols;
            double w = Math.Max(MinBoxW, Math.Max(title.W + 2 * HeaderPadX, cols.Sum(c => c.Width)));
            // Without attributes the box is just the name, given a little more air so it doesn't read as a flowchart node.
            double h = e.Attributes.Count == 0 ? headerH[i] + 10 : headerH[i] + e.Attributes.Count * rowH[i];
            sizes[i] = new MSize(w, h);
        }

        // ── Relationships ──
        var links = new List<(ErRelationship Rel, LayeredEdge Edge)>();
        var loops = new List<(int Entity, ErRelationship Rel, MSize Label)>();
        var loopRoom = new double[er.Entities.Count];
        foreach (var r in er.Relationships)
        {
            int a = index[r.A], b = index[r.B];
            var label = string.IsNullOrEmpty(r.Label) ? default : measure(r.Label, MermaidTextRole.EdgeLabel);
            if (a == b)
            {
                loops.Add((a, r, label));
                loopRoom[a] = Math.Max(loopRoom[a], LayeredLayout.SelfLoopRoom(vertical, LoopReach, label));
                continue;
            }
            links.Add((r, new LayeredEdge { From = a, To = b, Label = label }));
        }

        var nodes = sizes.Select((s, i) => new LayeredNode { Size = s, SelfLoopRoom = loopRoom[i] }).ToList();
        if (LayeredLayout.Run(nodes, links.Select(l => l.Edge).ToList(), er.Direction, [], new Dictionary<FlowSubgraph, MSize>(),
                Spacing) is null)
            return null;

        // ── Scene: relationships, entities, then labels on top ──
        var items = new List<MermaidSceneItem>();
        var labels = new List<MermaidSceneItem>();
        foreach (var (rel, e) in links)
        {
            if (e.Points.Count < 2) continue;
            var pts = e.Points.ToList();
            pts[0] = MermaidGeometry.Clip(FlowShape.Rect, nodes[e.From].Box, pts[1]);
            pts[^1] = MermaidGeometry.Clip(FlowShape.Rect, nodes[e.To].Box, pts[^2]);
            items.Add(new SceneLine(pts, Stroke(rel), rel.AtA, rel.AtB, pts.Count > 2, MermaidLineRole.Edge));
            if (e.LabelAt is { } at) FlowchartLayout.AddLabel(labels, rel.Label, at, e.Label, MermaidTextRole.EdgeLabel);
        }
        foreach (var (i, rel, label) in loops)
        {
            var (pts, labelAt) = LayeredLayout.SelfLoop(nodes[i].Box, vertical, LoopReach, label, LoopSpread);
            items.Add(new SceneLine(pts, Stroke(rel), rel.AtA, rel.AtB, true, MermaidLineRole.Edge));
            if (label.W > 0) FlowchartLayout.AddLabel(labels, rel.Label, labelAt, label, MermaidTextRole.EdgeLabel);
        }

        for (int i = 0; i < er.Entities.Count; i++)
            Entity(items, er.Entities[i], nodes[i].Box, headerH[i], rowH[i], columns[i], measure);

        items.AddRange(labels);
        return MermaidLayout.Normalize(items, Margin);
    }

    private static MermaidStroke Stroke(ErRelationship r) => r.Identifying ? MermaidStroke.Solid : MermaidStroke.Dotted;

    private static void Entity(List<MermaidSceneItem> items, ErEntity e, MRect box, double headerH, double rowH,
        List<Column> columns, Func<string, MermaidTextRole, MSize> measure)
    {
        items.Add(new SceneShape(FlowShape.Rect, box, MermaidShapeRole.Node));
        var title = measure(e.Label, MermaidTextRole.ErEntity);
        if (e.Attributes.Count == 0)
        {
            items.Add(new SceneText(e.Label, MRect.Centered(box.Center, title), MermaidTextRole.ErEntity));
            return;
        }

        // Bands sit a pixel inside the outline so they don't paint over its stroke.
        var header = new MRect(box.X + 1, box.Y + 1, box.W - 2, headerH - 1);
        items.Add(new SceneShape(FlowShape.Rect, header, MermaidShapeRole.ErHeader));
        items.Add(new SceneText(e.Label, MRect.Centered(new MPoint(box.CenterX, box.Y + headerH / 2), title), MermaidTextRole.ErEntity));

        // Columns share any spare width (when the title is wider than the table) so the rows still fill the box.
        double spare = (box.W - columns.Sum(c => c.Width)) / Math.Max(1, columns.Count);
        for (int r = 0; r < e.Attributes.Count; r++)
        {
            double top = box.Y + headerH + r * rowH;
            if (r % 2 == 1)
                items.Add(new SceneShape(FlowShape.Rect, new MRect(box.X + 1, top, box.W - 2, Math.Min(rowH, box.Bottom - 1 - top)),
                    MermaidShapeRole.ErRow));
            double x = box.X;
            foreach (var col in columns)
            {
                var text = col.Text(e.Attributes[r]);
                if (text.Length > 0)
                {
                    var t = measure(text, col.Role);
                    items.Add(new SceneText(text, new MRect(x + CellPadX, top + (rowH - t.H) / 2, t.W, t.H), col.Role, AlignLeft: true));
                }
                x += col.Width + spare;
            }
        }
    }
}

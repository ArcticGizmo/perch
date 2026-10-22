namespace Perch.Data.Mermaid;

/// <summary>
/// Lays a flowchart out on the shared <see cref="LayeredLayout"/>: each node is a box sized around its label for its
/// shape, each link an edge (a subgraph endpoint stands in for one of its members), then the edge ends are clipped to
/// the shapes' outlines — or to the subgraph's box for a link drawn to a subgraph — and the scene is assembled.
/// </summary>
internal static class FlowchartLayout
{
    private const double Margin = 6, LoopReach = 20;
    private const int MaxNodes = 400;

    public static MermaidScene? Layout(MermaidFlowchart fc, Func<string, MermaidTextRole, MSize> measure)
    {
        if (fc.Nodes.Count == 0 || fc.Nodes.Count > MaxNodes) return null;
        bool vertical = fc.Direction is FlowDirection.TopDown or FlowDirection.BottomUp;
        var index = new Dictionary<string, int>();
        for (int i = 0; i < fc.Nodes.Count; i++) index[fc.Nodes[i].Id] = i;

        // ── Links, with subgraph endpoints resolved to a member node ──
        var links = new List<(FlowEdge Flow, LayeredEdge Edge, FlowSubgraph? From, FlowSubgraph? To)>();
        var loops = new List<(int Node, FlowEdge Flow, MSize Label)>();
        var loopRoom = new double[fc.Nodes.Count];
        foreach (var e in fc.Edges)
        {
            var (u, fromSg) = Resolve(e.From);
            var (w, toSg) = Resolve(e.To);
            if (u < 0 || w < 0) continue;
            var label = string.IsNullOrEmpty(e.Label) ? default : measure(e.Label, MermaidTextRole.EdgeLabel);
            if (u == w)
            {
                if (fromSg is null && toSg is null)
                {
                    loops.Add((u, e, label));
                    loopRoom[u] = Math.Max(loopRoom[u], LayeredLayout.SelfLoopRoom(vertical, LoopReach, label));
                }
                continue;
            }
            links.Add((e, new LayeredEdge { From = u, To = w, MinLen = e.Length, Label = label }, fromSg, toSg));
        }

        (int, FlowSubgraph?) Resolve(string id)
        {
            if (index.TryGetValue(id, out var i)) return (i, null);
            var sg = fc.Subgraphs.Find(s => s.Id == id);
            if (sg is null) return (-1, null);
            int member = fc.Nodes.FindIndex(n => Within(n.Subgraph, sg));
            return member < 0 ? (-1, null) : (member, sg);
        }

        var nodes = fc.Nodes.Select((n, i) => new LayeredNode
        {
            Size = MermaidGeometry.ShapeSize(n.Shape, measure(n.Label, MermaidTextRole.Node)),
            Cluster = n.Subgraph,
            SelfLoopRoom = loopRoom[i],
        }).ToList();
        var titles = fc.Subgraphs.ToDictionary(s => s, s => string.IsNullOrEmpty(s.Title)
            ? new MSize(0, 0) : measure(s.Title, MermaidTextRole.ClusterTitle));

        var boxes = LayeredLayout.Run(nodes, links.Select(l => l.Edge).ToList(), fc.Direction, fc.Subgraphs, titles,
            LayeredSpacing.Default);
        if (boxes is null) return null;

        // ── Scene, in paint order: clusters, edges, nodes, then labels on top ──
        var items = new List<MermaidSceneItem>();
        // Titles paint with the edge labels, over the edges, so an edge entering a subgraph from above can't strike one out.
        var labels = new List<MermaidSceneItem>();
        foreach (var sg in fc.Subgraphs.OrderBy(s => s.Depth))
        {
            if (!boxes.TryGetValue(sg, out var box)) continue;
            items.Add(new SceneShape(FlowShape.Rect, box, MermaidShapeRole.Cluster));
            if (titles[sg].W > 0)
            {
                var at = new MRect(box.CenterX - titles[sg].W / 2, box.Y + LayeredLayout.TitleGap, titles[sg].W, titles[sg].H);
                labels.Add(new SceneShape(FlowShape.Rect, at.Inflate(4, 1, 4, 1), MermaidShapeRole.LabelBackground));
                labels.Add(new SceneText(sg.Title, at, MermaidTextRole.ClusterTitle));
            }
        }

        foreach (var (flow, e, fromSg, toSg) in links)
        {
            if (flow.Stroke == MermaidStroke.Invisible || e.Points.Count < 2) continue;
            var pts = e.Points.ToList();
            pts[0] = EndPoint(fc.Nodes[e.From], nodes[e.From], fromSg, pts[1], boxes);
            pts[^1] = EndPoint(fc.Nodes[e.To], nodes[e.To], toSg, pts[^2], boxes);
            items.Add(new SceneLine(pts, flow.Stroke, flow.StartHead, flow.EndHead, pts.Count > 2, MermaidLineRole.Edge));
            if (e.LabelAt is { } at)
                AddLabel(labels, flow.Label!, at, e.Label, MermaidTextRole.EdgeLabel);
        }

        foreach (var (i, flow, label) in loops)
        {
            if (flow.Stroke == MermaidStroke.Invisible) continue;
            var (pts, labelAt) = LayeredLayout.SelfLoop(nodes[i].Box, vertical, LoopReach, label);
            items.Add(new SceneLine(pts, flow.Stroke, flow.StartHead, flow.EndHead, true, MermaidLineRole.Edge));
            if (!string.IsNullOrEmpty(flow.Label)) AddLabel(labels, flow.Label, labelAt, label, MermaidTextRole.EdgeLabel);
        }

        for (int i = 0; i < fc.Nodes.Count; i++)
        {
            var n = fc.Nodes[i];
            var box = nodes[i].Box;
            items.Add(new SceneShape(n.Shape, box, MermaidShapeRole.Node));
            var t = measure(n.Label, MermaidTextRole.Node);
            items.Add(new SceneText(n.Label, MRect.Centered(new MPoint(box.CenterX, box.CenterY + TextNudge(n.Shape, box)), t), MermaidTextRole.Node));
        }

        items.AddRange(labels);
        return MermaidLayout.Normalize(items, Margin);
    }

    private static bool Within(FlowSubgraph? sg, FlowSubgraph target)
    {
        for (var s = sg; s is not null; s = s.Parent)
            if (s == target) return true;
        return false;
    }

    // A cylinder's label sits in the body below the top cap, not across the cap's rim.
    private static double TextNudge(FlowShape shape, MRect box) =>
        shape == FlowShape.Cylinder ? MermaidGeometry.CylinderCap(box.W) / 2 : 0;

    /// <summary>A label centred on <paramref name="at"/> over a background pill that hides the line beneath it.</summary>
    internal static void AddLabel(List<MermaidSceneItem> into, string text, MPoint at, MSize size, MermaidTextRole role)
    {
        into.Add(new SceneShape(FlowShape.Rect, MRect.Centered(at, new MSize(size.W + 8, size.H + 4)), MermaidShapeRole.LabelBackground));
        into.Add(new SceneText(text, MRect.Centered(at, size), role));
    }

    // Where an edge meets its endpoint: the node's outline, or the subgraph's box for an edge drawn to a subgraph.
    private static MPoint EndPoint(FlowNode n, LayeredNode v, FlowSubgraph? cluster, MPoint toward, Dictionary<FlowSubgraph, MRect> boxes)
    {
        if (cluster is not null && boxes.TryGetValue(cluster, out var box) && !box.Contains(toward)
            && MermaidGeometry.EnterRect(toward, v.Center, box) is { } hit)
            return hit;
        return MermaidGeometry.Clip(n.Shape, v.Box, toward);
    }
}

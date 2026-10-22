namespace Perch.Data.Mermaid;

/// <summary>A box to place. <see cref="Center"/> is filled in by <see cref="LayeredLayout.Run"/>.</summary>
internal sealed class LayeredNode
{
    public required MSize Size { get; init; }
    public FlowSubgraph? Cluster { get; init; }

    /// <summary>Extra room to keep after the node across the flow, for a self-loop and its label drawn beside it.</summary>
    public double SelfLoopRoom { get; init; }

    public MPoint Center { get; set; }
    public MRect Box => MRect.Centered(Center, Size);
}

/// <summary>A connection between two different nodes. <see cref="Points"/> and <see cref="LabelAt"/> are filled in by
/// <see cref="LayeredLayout.Run"/>: the centres the edge passes through from <see cref="From"/> to <see cref="To"/>,
/// endpoints included (the caller clips the ends to its shapes).</summary>
internal sealed class LayeredEdge
{
    public required int From { get; init; }
    public required int To { get; init; }
    public int MinLen { get; init; } = 1;

    /// <summary>The label's size; default (zero) for an unlabelled edge.</summary>
    public MSize Label { get; init; }

    public bool HasLabel => Label.W > 0;
    public List<MPoint> Points { get; } = new();
    public MPoint? LabelAt { get; set; }
}

internal readonly record struct LayeredSpacing(double NodeSep, double RankSep)
{
    public static readonly LayeredSpacing Default = new(34, 44);
}

/// <summary>
/// Places boxes and edges as a layered graph — the Sugiyama recipe dagre (and so Mermaid) uses, cut down to what a
/// Markdown preview needs: break cycles, rank nodes along the flow, thread long edges through dummy nodes, order each
/// rank to cut crossings, then place nodes across the flow so edges run as straight as their neighbours allow. Shared
/// by every Mermaid diagram that is boxes joined by lines (flowcharts, ER diagrams).
///
/// The work happens in two abstract axes — <em>main</em> (the flow direction, rank by rank) and <em>cross</em> (across
/// it) — and is mapped onto x/y only at the end, so TD/BT/LR/RL share one code path. Subgraphs stay contiguous in every
/// rank (ordering sorts cluster members as a block) and get room reserved for their padding and title, so their boxes
/// don't land on their neighbours. An edge label rides on a dummy node in the middle of its edge (a labelled edge
/// spans at least two ranks), so the label gets real space in the layout instead of being painted over a node.
/// </summary>
internal sealed class LayeredLayout
{
    public const double ClusterPad = 14, TitleGap = 4;
    private const double DummySep = 14;
    private const int MaxVertices = 2500, OrderIterations = 24, PlaceIterations = 8;

    private sealed class Vx
    {
        public int Index;
        public LayeredNode? Node;           // null for a dummy
        public MSize Size;                  // in x/y
        public double Main, Cross;          // extent along each axis
        public double MainPos, CrossPos;    // centre
        public int Rank, Order;
        public FlowSubgraph[] Clusters = []; // outermost first
        public double SelfLoopRoom;
        public readonly List<Vx> Preds = new(), Succs = new();
        public bool IsDummy => Node is null;
        public double X, Y;
        public MRect Box => MRect.Centered(new MPoint(X, Y), Size);
    }

    private sealed class Ed
    {
        public required LayeredEdge Edge;
        public required Vx U, V;            // as written (U → V)
        public int MinLen;
        public bool Reversed;
        public List<Vx> Chain = new();      // the effective (top→bottom) path, endpoints included
        public Vx? LabelVx;
        public Vx Top => Reversed ? V : U;
        public Vx Bottom => Reversed ? U : V;
    }

    private readonly LayeredSpacing _spacing;
    private readonly IReadOnlyDictionary<FlowSubgraph, MSize> _titles;
    private readonly bool _vertical, _titleAcross;

    private LayeredLayout(LayeredSpacing spacing, IReadOnlyDictionary<FlowSubgraph, MSize> titles, FlowDirection dir)
    {
        _spacing = spacing;
        _titles = titles;
        _vertical = dir is FlowDirection.TopDown or FlowDirection.BottomUp;
        _titleAcross = !_vertical;   // LR/RL: the title (on top) sits on the cross-axis start side
    }

    /// <summary>Places <paramref name="nodes"/> and routes <paramref name="edges"/> (filling their outputs) and returns
    /// each non-empty subgraph's box, or null when the graph is too big to lay out sensibly. <paramref name="titles"/>
    /// holds a measured title per subgraph (zero size for none).</summary>
    public static Dictionary<FlowSubgraph, MRect>? Run(IReadOnlyList<LayeredNode> nodes, IReadOnlyList<LayeredEdge> edges,
        FlowDirection direction, IReadOnlyList<FlowSubgraph> subgraphs, IReadOnlyDictionary<FlowSubgraph, MSize> titles,
        LayeredSpacing spacing) =>
        new LayeredLayout(spacing, titles, direction).RunCore(nodes, edges, direction, subgraphs);

    private Dictionary<FlowSubgraph, MRect>? RunCore(IReadOnlyList<LayeredNode> nodes, IReadOnlyList<LayeredEdge> edges,
        FlowDirection direction, IReadOnlyList<FlowSubgraph> subgraphs)
    {
        if (nodes.Count == 0) return new();

        var verts = new List<Vx>();
        foreach (var n in nodes)
        {
            var v = new Vx { Index = verts.Count, Node = n, Size = n.Size, Clusters = ChainOf(n.Cluster), SelfLoopRoom = n.SelfLoopRoom };
            SetAxes(v);
            verts.Add(v);
        }

        var eds = new List<Ed>();
        foreach (var e in edges)
        {
            if (e.From == e.To) continue;
            int min = Math.Max(1, e.MinLen);
            if (e.HasLabel) min = Math.Max(2, min);
            eds.Add(new Ed { Edge = e, U = verts[e.From], V = verts[e.To], MinLen = min });
        }

        BreakCycles(verts, eds);
        AssignRanks(verts, eds);

        // ── Thread edges through dummies, one vertex per rank they cross ──
        foreach (var e in eds)
        {
            var top = e.Top;
            var bottom = e.Bottom;
            var common = CommonPrefix(top.Clusters, bottom.Clusters);
            e.Chain.Add(top);
            int labelRank = e.Edge.HasLabel ? top.Rank + (bottom.Rank - top.Rank) / 2 : -1;
            for (int r = top.Rank + 1; r < bottom.Rank; r++)
            {
                var d = new Vx { Index = verts.Count, Rank = r, Clusters = common };
                if (r == labelRank)
                {
                    d.Size = new MSize(e.Edge.Label.W + 8, e.Edge.Label.H + 4);
                    e.LabelVx = d;
                }
                SetAxes(d);
                verts.Add(d);
                e.Chain.Add(d);
            }
            e.Chain.Add(bottom);
            for (int i = 0; i + 1 < e.Chain.Count; i++)
            {
                e.Chain[i].Succs.Add(e.Chain[i + 1]);
                e.Chain[i + 1].Preds.Add(e.Chain[i]);
            }
            if (verts.Count > MaxVertices) return null;
        }

        var layers = BuildLayers(verts);
        Order(layers);
        PlaceAcross(layers);
        PlaceAlong(layers, subgraphs, titleAtEnd: direction == FlowDirection.BottomUp);

        foreach (var v in verts)
            (v.X, v.Y) = direction switch
            {
                FlowDirection.TopDown => (v.CrossPos, v.MainPos),
                FlowDirection.BottomUp => (v.CrossPos, -v.MainPos),
                FlowDirection.LeftRight => (v.MainPos, v.CrossPos),
                _ => (-v.MainPos, v.CrossPos),
            };

        foreach (var v in verts)
            if (v.Node is { } n) n.Center = new MPoint(v.X, v.Y);
        foreach (var e in eds)
        {
            var path = e.Reversed ? Enumerable.Reverse(e.Chain) : e.Chain;
            e.Edge.Points.Clear();
            e.Edge.Points.AddRange(path.Select(v => new MPoint(v.X, v.Y)));
            e.Edge.LabelAt = e.LabelVx is { } lv ? new MPoint(lv.X, lv.Y) : null;
        }
        return ClusterBoxes(subgraphs, verts);
    }

    /// <summary>The path of a self-loop on box <paramref name="b"/> reaching <paramref name="reach"/> out from it
    /// (right of it in a vertical flow, below it in a horizontal one), its ends <paramref name="spread"/> either side of
    /// the box's middle, and where its label goes.</summary>
    public static (List<MPoint> Points, MPoint LabelAt) SelfLoop(MRect b, bool vertical, double reach, MSize label,
        double spread = 10)
    {
        if (vertical)
        {
            double s = Math.Max(4, Math.Min(spread, b.H / 2 - 3));
            double y0 = b.CenterY - s, y1 = b.CenterY + s;
            return ([new(b.Right, y0), new(b.Right + reach, y0 - 8), new(b.Right + reach, y1 + 8), new(b.Right, y1)],
                new MPoint(b.Right + reach + 4 + label.W / 2, b.CenterY));
        }
        double sx = Math.Max(4, Math.Min(spread, b.W / 2 - 3));
        double x0 = b.CenterX - sx, x1 = b.CenterX + sx;
        return ([new(x0, b.Bottom), new(x0 - 8, b.Bottom + reach), new(x1 + 8, b.Bottom + reach), new(x1, b.Bottom)],
            new MPoint(b.CenterX, b.Bottom + reach + 4 + label.H / 2));
    }

    /// <summary>The <see cref="LayeredNode.SelfLoopRoom"/> a node needs for a <see cref="SelfLoop"/> and its label.</summary>
    public static double SelfLoopRoom(bool vertical, double reach, MSize label) =>
        reach + 10 + (label.W > 0 ? (vertical ? label.W : label.H) + 8 : 0);

    private void SetAxes(Vx v)
    {
        v.Main = _vertical ? v.Size.H : v.Size.W;
        v.Cross = _vertical ? v.Size.W : v.Size.H;
    }

    private static FlowSubgraph[] ChainOf(FlowSubgraph? sg)
    {
        var chain = new List<FlowSubgraph>();
        for (var s = sg; s is not null; s = s.Parent) chain.Add(s);
        chain.Reverse();
        return chain.ToArray();
    }

    private static FlowSubgraph[] CommonPrefix(FlowSubgraph[] a, FlowSubgraph[] b)
    {
        int n = 0;
        while (n < a.Length && n < b.Length && a[n] == b[n]) n++;
        return a[..n];
    }

    // Reverses each edge a depth-first walk finds closing a cycle, so the ranked graph is acyclic. The edge is
    // still drawn in its written direction; only its ranking flips.
    private static void BreakCycles(List<Vx> verts, List<Ed> edges)
    {
        var outgoing = verts.ToDictionary(v => v, _ => new List<Ed>());
        foreach (var e in edges) outgoing[e.U].Add(e);
        var state = new Dictionary<Vx, int>();   // absent = unvisited, 1 = on the stack, 2 = done

        foreach (var root in verts)
        {
            if (state.ContainsKey(root)) continue;
            var stack = new Stack<(Vx V, int Next)>();
            stack.Push((root, 0));
            state[root] = 1;
            while (stack.Count > 0)
            {
                var (v, next) = stack.Pop();
                var outs = outgoing[v];
                if (next < outs.Count)
                {
                    stack.Push((v, next + 1));
                    var e = outs[next];
                    if (!state.TryGetValue(e.V, out var s)) { state[e.V] = 1; stack.Push((e.V, 0)); }
                    else if (s == 1) e.Reversed = true;
                }
                else state[v] = 2;
            }
        }
    }

    // Longest-path ranking honouring each edge's minimum length, then sources pulled down next to their first
    // successor so a node that only feeds the bottom of the chart doesn't sit stranded at the top.
    private static void AssignRanks(List<Vx> verts, List<Ed> edges)
    {
        var indeg = verts.ToDictionary(v => v, _ => 0);
        var outs = verts.ToDictionary(v => v, _ => new List<Ed>());
        foreach (var e in edges) { indeg[e.Bottom]++; outs[e.Top].Add(e); }

        var queue = new Queue<Vx>(verts.Where(v => indeg[v] == 0));
        var topo = new List<Vx>();
        foreach (var v in verts) v.Rank = 0;
        while (queue.Count > 0)
        {
            var v = queue.Dequeue();
            topo.Add(v);
            foreach (var e in outs[v])
            {
                e.Bottom.Rank = Math.Max(e.Bottom.Rank, v.Rank + e.MinLen);
                if (--indeg[e.Bottom] == 0) queue.Enqueue(e.Bottom);
            }
        }

        var hasIn = new HashSet<Vx>(edges.Select(e => e.Bottom));
        for (int i = topo.Count - 1; i >= 0; i--)
        {
            var v = topo[i];
            if (hasIn.Contains(v) || outs[v].Count == 0) continue;
            v.Rank = outs[v].Min(e => e.Bottom.Rank - e.MinLen);
        }
        int min = verts.Min(v => v.Rank);
        foreach (var v in verts) v.Rank -= min;
    }

    // The initial order of each rank: a depth-first walk from the nodes in the order they were written, so a chart
    // reads in roughly the order its author typed it.
    private static List<List<Vx>> BuildLayers(List<Vx> verts)
    {
        int maxRank = verts.Max(v => v.Rank);
        var layers = Enumerable.Range(0, maxRank + 1).Select(_ => new List<Vx>()).ToList();
        var seen = new HashSet<Vx>();
        foreach (var root in verts.Where(v => !v.IsDummy).OrderBy(v => v.Rank).ThenBy(v => v.Index))
        {
            var stack = new Stack<Vx>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var v = stack.Pop();
                if (!seen.Add(v)) continue;
                layers[v.Rank].Add(v);
                for (int i = v.Succs.Count - 1; i >= 0; i--) stack.Push(v.Succs[i]);
            }
        }
        foreach (var layer in layers)
        {
            Renumber(layer);
            var sorted = Grouped(layer, v => v.Order, 0);
            layer.Clear();
            layer.AddRange(sorted);
            Renumber(layer);
        }
        return layers;
    }

    private static void Renumber(List<Vx> layer)
    {
        for (int i = 0; i < layer.Count; i++) layer[i].Order = i;
    }

    // Barycentre sweeps, alternately down and up, keeping the ordering with the fewest crossings.
    private static void Order(List<List<Vx>> layers)
    {
        var best = layers.Select(l => l.ToList()).ToList();
        long bestCross = Crossings(layers);
        for (int it = 0; it < OrderIterations && bestCross > 0; it++)
        {
            bool down = it % 2 == 0;
            if (down)
                for (int r = 1; r < layers.Count; r++) Sweep(layers[r], v => v.Preds);
            else
                for (int r = layers.Count - 2; r >= 0; r--) Sweep(layers[r], v => v.Succs);
            long c = Crossings(layers);
            if (c < bestCross)
            {
                bestCross = c;
                best = layers.Select(l => l.ToList()).ToList();
            }
        }
        for (int r = 0; r < layers.Count; r++)
        {
            layers[r].Clear();
            layers[r].AddRange(best[r]);
            Renumber(layers[r]);
        }
    }

    private static void Sweep(List<Vx> layer, Func<Vx, List<Vx>> neighbours)
    {
        double Bary(Vx v)
        {
            var ns = neighbours(v);
            return ns.Count == 0 ? v.Order : ns.Average(n => n.Order);
        }
        var bary = layer.ToDictionary(v => v, Bary);
        var sorted = Grouped(layer, v => bary[v], 0);
        layer.Clear();
        layer.AddRange(sorted);
        Renumber(layer);
    }

    // Sorts a rank by key while keeping each subgraph's members together: members of one cluster sort as a block
    // (keyed by their mean), recursively for nested clusters, so a subgraph's box never has a stranger inside it.
    private static List<Vx> Grouped(List<Vx> items, Func<Vx, double> key, int depth)
    {
        var groups = new List<(double Key, int First, FlowSubgraph? Cluster, List<Vx> Members)>();
        var byCluster = new Dictionary<FlowSubgraph, int>();
        foreach (var v in items)
        {
            if (v.Clusters.Length > depth)
            {
                var c = v.Clusters[depth];
                if (!byCluster.TryGetValue(c, out var gi))
                {
                    byCluster[c] = gi = groups.Count;
                    groups.Add((0, v.Order, c, new List<Vx>()));
                }
                groups[gi].Members.Add(v);
            }
            else groups.Add((key(v), v.Order, null, [v]));
        }
        for (int i = 0; i < groups.Count; i++)
            if (groups[i].Cluster is not null)
                groups[i] = groups[i] with { Key = groups[i].Members.Average(key) };

        var outp = new List<Vx>(items.Count);
        foreach (var g in groups.OrderBy(g => g.Key).ThenBy(g => g.First))
            outp.AddRange(g.Cluster is null ? g.Members : Grouped(g.Members, key, depth + 1));
        return outp;
    }

    private static long Crossings(List<List<Vx>> layers)
    {
        long total = 0;
        var segs = new List<(int A, int B)>();
        for (int r = 0; r + 1 < layers.Count; r++)
        {
            segs.Clear();
            foreach (var v in layers[r])
                foreach (var s in v.Succs)
                    segs.Add((v.Order, s.Order));
            for (int i = 0; i < segs.Count; i++)
                for (int j = i + 1; j < segs.Count; j++)
                    if ((long)(segs[i].A - segs[j].A) * (segs[i].B - segs[j].B) < 0) total++;
        }
        return total;
    }

    // ── Cross-axis placement ──

    // The minimum centre-to-centre distance between neighbours a (before) and b (after) in one rank: half of each,
    // a gap (tighter between dummies, so bundled edges stay close), and room for every subgraph boundary between them.
    private double Separation(Vx a, Vx b)
    {
        double sep = _spacing.NodeSep;
        double gap = a.IsDummy && b.IsDummy ? DummySep : a.IsDummy || b.IsDummy ? (sep + DummySep) / 2 : sep;
        gap += a.SelfLoopRoom;
        foreach (var c in a.Clusters)
            if (!b.Clusters.Contains(c)) gap += ClusterPad;
        foreach (var c in b.Clusters)
            if (!a.Clusters.Contains(c)) gap += ClusterPad + (_titleAcross && _titles[c].H > 0 ? _titles[c].H + 2 * TitleGap : 0);
        return (a.Cross + b.Cross) / 2 + gap;
    }

    private void PlaceAcross(List<List<Vx>> layers)
    {
        foreach (var layer in layers)
        {
            double x = 0;
            for (int i = 0; i < layer.Count; i++)
            {
                if (i > 0) x += Separation(layer[i - 1], layer[i]);
                layer[i].CrossPos = x;
            }
        }
        for (int it = 0; it < PlaceIterations; it++)
        {
            for (int r = 1; r < layers.Count; r++) Settle(layers[r], v => v.Preds);
            for (int r = layers.Count - 2; r >= 0; r--) Settle(layers[r], v => v.Succs);
        }
        foreach (var layer in layers)
            Settle(layer, v => v.Preds.Concat(v.Succs).ToList());
    }

    // Moves a rank's vertices as close as the spacing allows to the mean position of their neighbours, keeping the
    // order: the least-squares fit under "each at least its separation after the last", solved exactly by pool-
    // adjacent-violators on positions offset by the cumulative separation.
    private void Settle(List<Vx> layer, Func<Vx, List<Vx>> neighbours)
    {
        int n = layer.Count;
        if (n == 0) return;
        var offset = new double[n];
        for (int i = 1; i < n; i++) offset[i] = offset[i - 1] + Separation(layer[i - 1], layer[i]);

        var blocks = new List<(double Sum, double Weight, int Count)>();
        for (int i = 0; i < n; i++)
        {
            var v = layer[i];
            var ns = neighbours(v);
            double want = ns.Count == 0 ? v.CrossPos : ns.Average(x => x.CrossPos);
            double w = v.IsDummy ? 2 : 1;   // long edges pull harder, so they straighten
            blocks.Add(((want - offset[i]) * w, w, 1));
            while (blocks.Count > 1 && blocks[^2].Sum / blocks[^2].Weight > blocks[^1].Sum / blocks[^1].Weight)
            {
                var last = blocks[^1];
                var prev = blocks[^2];
                blocks.RemoveAt(blocks.Count - 1);
                blocks[^1] = (prev.Sum + last.Sum, prev.Weight + last.Weight, prev.Count + last.Count);
            }
        }
        int k = 0;
        foreach (var (sum, weight, count) in blocks)
        {
            double y = sum / weight;
            for (int j = 0; j < count; j++, k++) layer[k].CrossPos = y + offset[k];
        }
    }

    // ── Main-axis placement ──

    private void PlaceAlong(List<List<Vx>> layers, IReadOnlyList<FlowSubgraph> subgraphs, bool titleAtEnd)
    {
        int count = layers.Count;
        var before = new double[count];
        var after = new double[count];
        foreach (var sg in subgraphs)
        {
            var ranks = layers.SelectMany(l => l).Where(v => v.Clusters.Contains(sg)).Select(v => v.Rank).ToList();
            if (ranks.Count == 0) continue;
            // In TD the title sits above the box (the start of the flow); in BT it's still on top, which is the end.
            double title = _vertical && _titles[sg].H > 0 ? _titles[sg].H + 2 * TitleGap : 0;
            before[ranks.Min()] += ClusterPad + (titleAtEnd ? 0 : title);
            after[ranks.Max()] += ClusterPad + (titleAtEnd ? title : 0);
        }

        double pos = 0;
        for (int r = 0; r < count; r++)
        {
            double thick = layers[r].Count == 0 ? 0 : layers[r].Max(v => v.Main);
            if (r > 0)
            {
                double prev = layers[r - 1].Count == 0 ? 0 : layers[r - 1].Max(v => v.Main);
                pos += prev / 2 + _spacing.RankSep + after[r - 1] + before[r] + thick / 2;
            }
            foreach (var v in layers[r]) v.MainPos = pos;
        }
    }

    // Each subgraph's box: its members (nodes, the dummies of edges inside it, and nested boxes) plus padding, with
    // the title on top and at least the title's width.
    private Dictionary<FlowSubgraph, MRect> ClusterBoxes(IReadOnlyList<FlowSubgraph> subgraphs, List<Vx> verts)
    {
        var boxes = new Dictionary<FlowSubgraph, MRect>();
        foreach (var sg in subgraphs.OrderByDescending(s => s.Depth))
        {
            MRect? r = null;
            foreach (var v in verts)
                if (v.Clusters.Contains(sg))
                    r = r is { } a ? a.Union(v.Box) : v.Box;
            foreach (var child in subgraphs)
                if (child.Parent == sg && boxes.TryGetValue(child, out var cb))
                    r = r is { } a ? a.Union(cb) : cb;
            if (r is not { } box) continue;

            var t = _titles[sg];
            box = box.Inflate(ClusterPad, ClusterPad + (t.H > 0 ? t.H + 2 * TitleGap : 0), ClusterPad, ClusterPad);
            double minW = t.W + 2 * ClusterPad;
            if (box.W < minW) box = box.Inflate((minW - box.W) / 2, 0, (minW - box.W) / 2, 0);
            boxes[sg] = box;
        }
        return boxes;
    }
}

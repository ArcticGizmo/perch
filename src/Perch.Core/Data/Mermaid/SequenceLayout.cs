namespace Perch.Data.Mermaid;

/// <summary>
/// Lays a sequence diagram out the way Mermaid does: participants as columns (boxes on top, mirrored at the bottom,
/// joined by lifelines), then one row per message, note, and block header, top to bottom. Columns are first spaced
/// by their boxes, then pushed apart wherever a message label or note wouldn't fit between them. Blocks
/// (<c>loop</c>, <c>alt</c>, …) are framed around the columns their contents touch, widening outward per nesting level.
/// </summary>
internal static class SequenceLayout
{
    private const double BoxPadX = 16, BoxPadY = 10, MinBoxW = 90, ColGap = 40, LabelGap = 6, RowGap = 16;
    private const double NotePad = 8, BarHalf = 5, BarStep = 4, SelfLoopW = 30, BlockInset = 22, BlockStep = 10;
    private const double ActorFigure = 34, Margin = 6;
    private const int MaxItems = 1500;

    private sealed class Block
    {
        public required SeqBlockStart Start;
        public double Top;
        public double MinX = double.MaxValue, MaxX = double.MinValue;
        public double HeaderW;
        public int InnerDepth;
        public readonly List<(double Y, string Label)> Dividers = new();
        public readonly List<Block> Children = new();
    }

    public static MermaidScene? Layout(MermaidSequence seq, Func<string, MermaidTextRole, MSize> measure)
    {
        int n = seq.Participants.Count;
        if (n == 0 || seq.Items.Count > MaxItems) return null;
        var index = new Dictionary<string, int>();
        for (int i = 0; i < n; i++) index[seq.Participants[i].Id] = i;

        // ── Participant boxes ──
        var labelSize = seq.Participants.Select(p => measure(p.Label, MermaidTextRole.Actor)).ToArray();
        var boxW = new double[n];
        double headH = 0;
        for (int i = 0; i < n; i++)
        {
            var p = seq.Participants[i];
            boxW[i] = Math.Max(labelSize[i].W + 2 * BoxPadX, p.IsActor ? 50 : MinBoxW);
            headH = Math.Max(headH, p.IsActor ? ActorFigure + labelSize[i].H + 4 : labelSize[i].H + 2 * BoxPadY);
        }

        // ── Column spacing ──
        var gap = new double[Math.Max(0, n - 1)];
        for (int i = 0; i + 1 < n; i++) gap[i] = boxW[i] / 2 + boxW[i + 1] / 2 + ColGap;
        double leftExtra = 0, rightExtra = 0;

        double Span(int lo, int hi) { double s = 0; for (int k = lo; k < hi; k++) s += gap[k]; return s; }
        void Need(int lo, int hi, double need)
        {
            if (lo == hi) return;
            double have = Span(lo, hi);
            if (have >= need) return;
            double each = (need - have) / (hi - lo);
            for (int k = lo; k < hi; k++) gap[k] += each;
        }
        void NeedRight(int i, double need) { if (i + 1 < n) Need(i, i + 1, need); else rightExtra = Math.Max(rightExtra, need); }
        void NeedLeft(int i, double need) { if (i > 0) Need(i - 1, i, need); else leftExtra = Math.Max(leftExtra, need); }

        var needs = new List<(int Lo, int Hi, double W)>();
        foreach (var item in seq.Items)
            switch (item)
            {
                case SeqMessage m:
                {
                    int a = index[m.From], b = index[m.To];
                    var t = measure(m.Text, MermaidTextRole.Message);
                    if (a == b) NeedRight(a, SelfLoopW + 8 + t.W + 12);
                    else needs.Add((Math.Min(a, b), Math.Max(a, b), t.W + 24 + (seq.AutoNumber ? 20 : 0)));
                    break;
                }
                case SeqNote note:
                {
                    var t = measure(note.Text, MermaidTextRole.Note);
                    double w = t.W + 2 * NotePad;
                    int a = index[note.A];
                    if (note.Placement == SeqNotePlacement.RightOf) NeedRight(a, w + 16);
                    else if (note.Placement == SeqNotePlacement.LeftOf) NeedLeft(a, w + 16);
                    else if (note.B is { } bId && index[bId] != a)
                        needs.Add((Math.Min(a, index[bId]), Math.Max(a, index[bId]), w - 30));
                    break;
                }
            }
        // Short spans first, so a long message spreads only the room its inner columns didn't already make.
        foreach (var (lo, hi, w) in needs.OrderBy(x => x.Hi - x.Lo)) Need(lo, hi, w);

        var cx = new double[n];
        cx[0] = leftExtra + boxW[0] / 2;
        for (int i = 1; i < n; i++) cx[i] = cx[i - 1] + gap[i - 1];

        // ── Rows ──
        var body = new List<MermaidSceneItem>();     // messages, notes, numbers
        var bars = new List<(int P, double Y0, double Y1, int Level)>();
        var active = Enumerable.Range(0, n).Select(_ => new Stack<double>()).ToArray();
        var blocks = new List<Block>();
        var open = new Stack<Block>();
        // Blocks need their final height and the width of everything inside, so they're framed after the rows.
        var blockBottoms = new Dictionary<Block, double>();
        void Close(Block b, double bottom) => blockBottoms[b] = bottom;
        double y = headH + RowGap + 4;
        int number = 0;

        void Touch(double x0, double x1)
        {
            foreach (var b in open) { b.MinX = Math.Min(b.MinX, x0); b.MaxX = Math.Max(b.MaxX, x1); }
        }
        void Activate(int p, double at) => active[p].Push(at);
        void Deactivate(int p, double at)
        {
            if (active[p].Count == 0) return;
            double from = active[p].Pop();
            bars.Add((p, from, Math.Max(at, from + 8), active[p].Count));
        }
        double Edge(int p, int dir) => cx[p] + (active[p].Count > 0 ? dir * (BarHalf + (active[p].Count - 1) * BarStep) : 0);

        foreach (var item in seq.Items)
            switch (item)
            {
                case SeqMessage m:
                {
                    int a = index[m.From], b = index[m.To];
                    var t = measure(m.Text, MermaidTextRole.Message);
                    var head = m.Head;
                    var startHead = m.Bidirectional ? head : MermaidHead.None;
                    if (a == b)
                    {
                        double y0 = y + 4, y1 = y0 + Math.Max(t.H, 14) + 10;
                        double x0 = Edge(a, 1);
                        body.Add(new SceneLine([new(x0, y0), new(x0 + SelfLoopW, y0), new(x0 + SelfLoopW, y1), new(x0, y1)],
                            m.Stroke, startHead, head, false, MermaidLineRole.Message));
                        var lb = new MRect(x0 + SelfLoopW + 8, (y0 + y1) / 2 - t.H / 2, t.W, t.H);
                        if (t.W > 0) body.Add(new SceneText(m.Text, lb, MermaidTextRole.Message, AlignLeft: true));
                        Touch(cx[a] - BarHalf, lb.Right);
                        if (seq.AutoNumber) Number(body, ++number, new MPoint(cx[a], y0), measure);
                        if (m.Activate) Activate(b, y0);
                        if (m.Deactivate) Deactivate(a, y1);
                        y = y1 + RowGap;
                    }
                    else
                    {
                        int dir = b > a ? 1 : -1;
                        double lineY = y + t.H + LabelGap;
                        if (m.Activate) Activate(b, lineY);
                        double x0 = Edge(a, dir), x1 = Edge(b, -dir);
                        body.Add(new SceneLine([new(x0, lineY), new(x1, lineY)], m.Stroke, startHead, head, false, MermaidLineRole.Message));
                        if (t.W > 0)
                            body.Add(new SceneText(m.Text, new MRect((x0 + x1) / 2 - t.W / 2, y, t.W, t.H), MermaidTextRole.Message));
                        Touch(Math.Min(x0, x1), Math.Max(x0, x1));
                        if (seq.AutoNumber) Number(body, ++number, new MPoint(x0, lineY), measure);
                        if (m.Deactivate) Deactivate(a, lineY);
                        y = lineY + RowGap;
                    }
                    break;
                }
                case SeqNote note:
                {
                    var t = measure(note.Text, MermaidTextRole.Note);
                    double w = t.W + 2 * NotePad, h = t.H + 2 * NotePad;
                    int a = index[note.A];
                    MRect box;
                    if (note.Placement == SeqNotePlacement.RightOf) box = new MRect(cx[a] + 10, y, w, h);
                    else if (note.Placement == SeqNotePlacement.LeftOf) box = new MRect(cx[a] - 10 - w, y, w, h);
                    else
                    {
                        int b = note.B is { } bId ? index[bId] : a;
                        double l = Math.Min(cx[a], cx[b]) - 22, r = Math.Max(cx[a], cx[b]) + 22;
                        double width = Math.Max(w, r - l);
                        box = new MRect((l + r) / 2 - width / 2, y, width, h);
                    }
                    body.Add(new SceneShape(FlowShape.Rect, box, MermaidShapeRole.Note));
                    body.Add(new SceneText(note.Text, MRect.Centered(box.Center, t), MermaidTextRole.Note));
                    Touch(box.X, box.Right);
                    y = box.Bottom + RowGap;
                    break;
                }
                case SeqActivation act:
                {
                    int p = index[act.Participant];
                    if (act.On) Activate(p, y - RowGap / 2);
                    else Deactivate(p, y - RowGap / 2);
                    break;
                }
                case SeqBlockStart start:
                {
                    var block = new Block { Start = start, Top = y };
                    var tag = start.Kind == "rect" ? default : measure(start.Kind, MermaidTextRole.BlockTag);
                    var label = string.IsNullOrEmpty(start.Label) ? default : measure($"[{start.Label}]", MermaidTextRole.BlockLabel);
                    block.HeaderW = tag.W + 16 + (label.W > 0 ? label.W + 12 : 0);
                    if (open.TryPeek(out var parent)) parent.Children.Add(block);
                    blocks.Add(block);
                    open.Push(block);
                    y += Math.Max(tag.H + 6, label.H) + 10;
                    break;
                }
                case SeqBlockDivider div:
                {
                    if (!open.TryPeek(out var block)) break;
                    y += 2;
                    block.Dividers.Add((y, div.Label));
                    var label = string.IsNullOrEmpty(div.Label) ? default : measure($"[{div.Label}]", MermaidTextRole.BlockLabel);
                    y += label.H + 10;
                    break;
                }
                case SeqBlockEnd:
                {
                    if (!open.TryPop(out var block)) break;
                    y += 4;
                    Close(block, y);
                    y += RowGap - 4;
                    break;
                }
            }
        while (open.TryPop(out var unclosed)) Close(unclosed, y);
        y += 4;
        for (int p = 0; p < n; p++)
            while (active[p].Count > 0) Deactivate(p, y);

        foreach (var b in Enumerable.Reverse(blocks))
            b.InnerDepth = b.Children.Count == 0 ? 0 : b.Children.Max(c => c.InnerDepth) + 1;

        var frames = new List<MermaidSceneItem>();
        foreach (var b in blocks)
        {
            if (b.MinX == double.MaxValue) { b.MinX = cx[0]; b.MaxX = cx[^1]; }
            double inset = BlockInset + b.InnerDepth * BlockStep;
            double x0 = b.MinX - inset, x1 = Math.Max(b.MaxX + inset, x0 + b.HeaderW + 8);
            var box = new MRect(x0, b.Top, x1 - x0, blockBottoms[b] - b.Top);
            bool rect = b.Start.Kind == "rect";
            frames.Add(new SceneShape(FlowShape.Rect, box, rect ? MermaidShapeRole.Cluster : MermaidShapeRole.Block));
            if (rect) continue;

            var tag = measure(b.Start.Kind, MermaidTextRole.BlockTag);
            var tagBox = new MRect(x0, b.Top, tag.W + 16, tag.H + 6);
            frames.Add(new SceneShape(FlowShape.Rect, tagBox, MermaidShapeRole.BlockTag));
            frames.Add(new SceneText(b.Start.Kind, MRect.Centered(tagBox.Center, tag), MermaidTextRole.BlockTag));
            if (!string.IsNullOrEmpty(b.Start.Label))
            {
                var text = $"[{b.Start.Label}]";
                var ls = measure(text, MermaidTextRole.BlockLabel);
                frames.Add(new SceneText(text, new MRect(tagBox.Right + 12, tagBox.CenterY - ls.H / 2, ls.W, ls.H),
                    MermaidTextRole.BlockLabel, AlignLeft: true));
            }
            foreach (var (dy, label) in b.Dividers)
            {
                frames.Add(new SceneLine([new(x0, dy), new(x1, dy)], MermaidStroke.Dotted, MermaidHead.None, MermaidHead.None,
                    false, MermaidLineRole.Divider));
                if (string.IsNullOrEmpty(label)) continue;
                var text = $"[{label}]";
                var ls = measure(text, MermaidTextRole.BlockLabel);
                frames.Add(new SceneText(text, new MRect((x0 + x1) / 2 - ls.W / 2, dy + 4, ls.W, ls.H), MermaidTextRole.BlockLabel));
            }
        }

        // ── Assemble: lifelines, activation bars, block frames (their tags and labels must read over the bars),
        // messages and notes, then the participant boxes ──
        double footY = y + RowGap;
        var items = new List<MermaidSceneItem>();
        for (int i = 0; i < n; i++)
            items.Add(new SceneLine([new(cx[i], headH), new(cx[i], footY)], MermaidStroke.Solid, MermaidHead.None,
                MermaidHead.None, false, MermaidLineRole.Lifeline));
        foreach (var (p, y0, y1, level) in bars)
            items.Add(new SceneShape(FlowShape.Rect, new MRect(cx[p] - BarHalf + level * BarStep, y0, 2 * BarHalf, y1 - y0),
                MermaidShapeRole.Activation));
        items.AddRange(frames);
        items.AddRange(body);
        for (int i = 0; i < n; i++)
        {
            Head(items, seq.Participants[i], new MRect(cx[i] - boxW[i] / 2, 0, boxW[i], headH), labelSize[i]);
            Head(items, seq.Participants[i], new MRect(cx[i] - boxW[i] / 2, footY, boxW[i], headH), labelSize[i]);
        }
        // A trailing self-message or note hangs right of the last column; keep it inside the canvas.
        var extent = new MRect(0, 0, cx[^1] + Math.Max(boxW[^1] / 2, rightExtra), footY + headH);
        return MermaidLayout.Normalize(items, Margin, extent);
    }

    private static void Head(List<MermaidSceneItem> items, SeqParticipant p, MRect box, MSize label)
    {
        if (p.IsActor)
        {
            var figure = new MRect(box.CenterX - 12, box.Y, 24, ActorFigure - 4);
            items.Add(new SceneShape(FlowShape.Rect, figure, MermaidShapeRole.Actor, Stick: true));
            items.Add(new SceneText(p.Label, new MRect(box.CenterX - label.W / 2, box.Bottom - label.H, label.W, label.H), MermaidTextRole.Actor));
            return;
        }
        items.Add(new SceneShape(FlowShape.Rect, box, MermaidShapeRole.Actor));
        items.Add(new SceneText(p.Label, MRect.Centered(box.Center, label), MermaidTextRole.Actor));
    }

    private static void Number(List<MermaidSceneItem> items, int n, MPoint at, Func<string, MermaidTextRole, MSize> measure)
    {
        var text = n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var t = measure(text, MermaidTextRole.Number);
        double d = Math.Max(16, t.W + 6);
        items.Add(new SceneShape(FlowShape.Circle, MRect.Centered(at, new MSize(d, d)), MermaidShapeRole.Number));
        items.Add(new SceneText(text, MRect.Centered(at, t), MermaidTextRole.Number));
    }
}

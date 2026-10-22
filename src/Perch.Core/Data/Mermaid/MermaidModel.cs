namespace Perch.Data.Mermaid;

/// <summary>A parsed diagram the preview knows how to lay out. Each kind has its own model and layout; both lay
/// out into the same <see cref="MermaidScene"/>, so the UI has one painter.</summary>
internal abstract class MermaidDiagram;

/// <summary>The outcome of <see cref="MermaidParser.Parse"/>: a diagram, or the reason there isn't one.
/// <see cref="Kind"/> is the header keyword (<c>flowchart</c>, <c>pie</c>, …) even when it's unsupported, so the UI
/// can say which diagram type it fell back on.</summary>
internal sealed record MermaidParse(MermaidDiagram? Diagram, string Kind, string? Error);

public enum MermaidStroke { Solid, Dotted, Thick, Invisible }

/// <summary>What sits at a line's end. The <c>Er…</c> heads are ER-diagram crow's-foot cardinality markers.</summary>
public enum MermaidHead { None, Arrow, Open, Circle, Cross, ErZeroOrOne, ErExactlyOne, ErZeroOrMore, ErOneOrMore }

// ── Flowchart ──────────────────────────────────────────────────────────────────────────────────────────

public enum FlowDirection { TopDown, BottomUp, LeftRight, RightLeft }

public enum FlowShape
{
    Rect, Round, Stadium, Subroutine, Cylinder, Circle, DoubleCircle, Asymmetric, Rhombus, Hexagon,
    Parallelogram, ParallelogramAlt, Trapezoid, TrapezoidAlt,
}

internal sealed class FlowNode(string id, string label, FlowShape shape)
{
    public string Id { get; } = id;
    public string Label { get; set; } = label;
    public FlowShape Shape { get; set; } = shape;

    /// <summary>True once the node was written with a shape or label, not merely referenced by id — a bare
    /// reference that names a subgraph is an edge to that subgraph, not a new node.</summary>
    public bool Explicit { get; set; }

    /// <summary>The innermost subgraph the node belongs to, or null at the top level.</summary>
    public FlowSubgraph? Subgraph { get; set; }
}

/// <param name="Length">How many ranks the link asks to span (<c>--></c> is 1, <c>---></c> 2, …).</param>
internal sealed record FlowEdge(
    string From, string To, string? Label, MermaidStroke Stroke, MermaidHead StartHead, MermaidHead EndHead, int Length);

internal sealed class FlowSubgraph(string id, string title, FlowSubgraph? parent)
{
    public string Id { get; } = id;
    public string Title { get; } = title;
    public FlowSubgraph? Parent { get; } = parent;
    public int Depth => Parent is null ? 0 : Parent.Depth + 1;
}

internal sealed class MermaidFlowchart : MermaidDiagram
{
    public FlowDirection Direction { get; set; } = FlowDirection.TopDown;
    public List<FlowNode> Nodes { get; } = new();
    public List<FlowEdge> Edges { get; } = new();
    public List<FlowSubgraph> Subgraphs { get; } = new();

    public FlowNode? Find(string id) => Nodes.Find(n => n.Id == id);
}

// ── Entity-relationship diagram ────────────────────────────────────────────────────────────────────────

/// <param name="Keys">The key markers as written (<c>PK</c>, <c>FK</c>, <c>UK</c>, comma-joined), or empty.</param>
internal sealed record ErAttribute(string Type, string Name, string Keys, string Comment);

internal sealed class ErEntity(string id)
{
    public string Id { get; } = id;
    public string Label { get; set; } = id;
    public List<ErAttribute> Attributes { get; } = new();
}

/// <param name="AtA">The cardinality marker at <paramref name="A"/>'s end (the left token of <c>||--o{</c>).</param>
/// <param name="Identifying">A solid <c>--</c> line; a dashed <c>..</c> one is non-identifying.</param>
internal sealed record ErRelationship(string A, string B, MermaidHead AtA, MermaidHead AtB, bool Identifying, string Label);

internal sealed class MermaidEr : MermaidDiagram
{
    public FlowDirection Direction { get; set; } = FlowDirection.TopDown;
    public List<ErEntity> Entities { get; } = new();
    public List<ErRelationship> Relationships { get; } = new();

    public ErEntity Ensure(string id)
    {
        var e = Entities.Find(x => x.Id == id);
        if (e is null) Entities.Add(e = new ErEntity(id));
        return e;
    }
}

// ── Sequence diagram ───────────────────────────────────────────────────────────────────────────────────

internal sealed class SeqParticipant(string id, string label, bool isActor)
{
    public string Id { get; } = id;
    public string Label { get; set; } = label;
    public bool IsActor { get; set; } = isActor;
}

internal enum SeqNotePlacement { LeftOf, RightOf, Over }

internal abstract record SeqItem;

/// <param name="Activate">A <c>+</c> after the arrow: the target starts an activation.</param>
/// <param name="Deactivate">A <c>-</c> after the arrow: the sender ends its activation.</param>
internal sealed record SeqMessage(
    string From, string To, string Text, MermaidStroke Stroke, MermaidHead Head, bool Bidirectional,
    bool Activate, bool Deactivate) : SeqItem;

internal sealed record SeqNote(SeqNotePlacement Placement, string A, string? B, string Text) : SeqItem;

internal sealed record SeqActivation(string Participant, bool On) : SeqItem;

/// <summary>A <c>loop</c>/<c>alt</c>/<c>opt</c>/<c>par</c>/<c>critical</c>/<c>break</c>/<c>rect</c> block opening.</summary>
internal sealed record SeqBlockStart(string Kind, string Label) : SeqItem;

/// <summary>An <c>else</c>/<c>and</c>/<c>option</c> section break inside the open block.</summary>
internal sealed record SeqBlockDivider(string Label) : SeqItem;

internal sealed record SeqBlockEnd : SeqItem;

internal sealed class MermaidSequence : MermaidDiagram
{
    public List<SeqParticipant> Participants { get; } = new();
    public List<SeqItem> Items { get; } = new();
    public bool AutoNumber { get; set; }

    public SeqParticipant Ensure(string id)
    {
        var p = Participants.Find(x => x.Id == id);
        if (p is null) Participants.Add(p = new SeqParticipant(id, id, isActor: false));
        return p;
    }
}

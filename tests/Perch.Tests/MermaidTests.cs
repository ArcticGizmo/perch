using Perch.Data.Mermaid;
using Xunit;

namespace Perch.Tests;

public class MermaidTests
{
    // A stand-in for real text shaping: 7px per character, 16px per line.
    private static MSize Measure(string text, MermaidTextRole role)
    {
        var lines = text.Split('\n');
        return new MSize(lines.Max(l => l.Length) * 7, 16 * lines.Length);
    }

    private static MermaidFlowchart Flow(string src)
    {
        var parse = MermaidParser.Parse(src);
        return Assert.IsType<MermaidFlowchart>(parse.Diagram);
    }

    private static MermaidSequence Seq(string src)
    {
        var parse = MermaidParser.Parse(src);
        return Assert.IsType<MermaidSequence>(parse.Diagram);
    }

    // ── Parsing: headers ──

    [Theory]
    [InlineData("graph TD\nA-->B", FlowDirection.TopDown)]
    [InlineData("graph TB\nA-->B", FlowDirection.TopDown)]
    [InlineData("flowchart LR\nA-->B", FlowDirection.LeftRight)]
    [InlineData("flowchart RL\nA-->B", FlowDirection.RightLeft)]
    [InlineData("graph BT\nA-->B", FlowDirection.BottomUp)]
    [InlineData("graph\nA-->B", FlowDirection.TopDown)]
    [InlineData("graph LR;A-->B;B-->C", FlowDirection.LeftRight)]
    public void Reads_the_flowchart_direction(string src, FlowDirection expected) =>
        Assert.Equal(expected, Flow(src).Direction);

    [Fact]
    public void Unsupported_diagram_types_name_themselves_without_an_error()
    {
        var parse = MermaidParser.Parse("pie title Pets\n  \"Dogs\" : 3");
        Assert.Null(parse.Diagram);
        Assert.Equal("pie", parse.Kind);
        Assert.Null(parse.Error);
    }

    [Fact]
    public void An_empty_block_is_an_error_not_a_crash()
    {
        var parse = MermaidParser.Parse("  \n%% just a comment\n");
        Assert.Null(parse.Diagram);
        Assert.NotNull(parse.Error);
    }

    [Fact]
    public void Skips_frontmatter_init_directives_and_comments()
    {
        var fc = Flow("---\ntitle: Hello\n---\n%%{init: {'theme':'dark'}}%%\nflowchart LR\n%% a comment\nA-->B");
        Assert.Equal(FlowDirection.LeftRight, fc.Direction);
        Assert.Equal(["A", "B"], fc.Nodes.Select(n => n.Id));
    }

    // ── Parsing: nodes ──

    [Theory]
    [InlineData("A[x]", FlowShape.Rect)]
    [InlineData("A(x)", FlowShape.Round)]
    [InlineData("A([x])", FlowShape.Stadium)]
    [InlineData("A[[x]]", FlowShape.Subroutine)]
    [InlineData("A[(x)]", FlowShape.Cylinder)]
    [InlineData("A((x))", FlowShape.Circle)]
    [InlineData("A(((x)))", FlowShape.DoubleCircle)]
    [InlineData("A>x]", FlowShape.Asymmetric)]
    [InlineData("A{x}", FlowShape.Rhombus)]
    [InlineData("A{{x}}", FlowShape.Hexagon)]
    [InlineData("A[/x/]", FlowShape.Parallelogram)]
    [InlineData("A[\\x\\]", FlowShape.ParallelogramAlt)]
    [InlineData("A[/x\\]", FlowShape.Trapezoid)]
    [InlineData("A[\\x/]", FlowShape.TrapezoidAlt)]
    [InlineData("A@{ shape: diamond, label: \"x\" }", FlowShape.Rhombus)]
    public void Reads_every_node_shape(string node, FlowShape expected)
    {
        var n = Assert.Single(Flow("flowchart TD\n" + node).Nodes);
        Assert.Equal("A", n.Id);
        Assert.Equal("x", n.Label);
        Assert.Equal(expected, n.Shape);
    }

    [Fact]
    public void A_bare_node_is_labelled_with_its_id()
    {
        var n = Assert.Single(Flow("graph TD\nlonely").Nodes);
        Assert.Equal("lonely", n.Label);
        Assert.Equal(FlowShape.Rect, n.Shape);
    }

    [Fact]
    public void Quoted_labels_keep_brackets_and_semicolons()
    {
        var fc = Flow("graph TD\nA[\"a [b] (c); d\"] --> B");
        Assert.Equal("a [b] (c); d", fc.Find("A")!.Label);
        Assert.Single(fc.Edges);
    }

    [Fact]
    public void Labels_turn_br_into_line_breaks_and_decode_entities()
    {
        var fc = Flow("graph TD\nA[one<br/>two] --> B[say #quot;hi#quot; &amp; go]");
        Assert.Equal("one\ntwo", fc.Find("A")!.Label);
        Assert.Equal("say \"hi\" & go", fc.Find("B")!.Label);
    }

    [Fact]
    public void A_later_definition_updates_an_earlier_reference()
    {
        var fc = Flow("graph TD\nA --> B\nB{Bee}");
        var b = fc.Find("B")!;
        Assert.Equal("Bee", b.Label);
        Assert.Equal(FlowShape.Rhombus, b.Shape);
        Assert.Equal(2, fc.Nodes.Count);
    }

    [Fact]
    public void Hyphenated_ids_and_class_suffixes()
    {
        var fc = Flow("graph LR\nnode-1[Start]:::hot-->node-2\nclassDef hot fill:#f00\nstyle node-2 stroke:#0f0");
        Assert.Equal(["node-1", "node-2"], fc.Nodes.Select(n => n.Id));
        var e = Assert.Single(fc.Edges);
        Assert.Equal(("node-1", "node-2"), (e.From, e.To));
    }

    // ── Parsing: links ──

    [Theory]
    [InlineData("A-->B", MermaidStroke.Solid, MermaidHead.None, MermaidHead.Arrow, 1)]
    [InlineData("A --- B", MermaidStroke.Solid, MermaidHead.None, MermaidHead.None, 1)]
    [InlineData("A ---> B", MermaidStroke.Solid, MermaidHead.None, MermaidHead.Arrow, 2)]
    [InlineData("A-.->B", MermaidStroke.Dotted, MermaidHead.None, MermaidHead.Arrow, 1)]
    [InlineData("A -.- B", MermaidStroke.Dotted, MermaidHead.None, MermaidHead.None, 1)]
    [InlineData("A -..-> B", MermaidStroke.Dotted, MermaidHead.None, MermaidHead.Arrow, 2)]
    [InlineData("A==>B", MermaidStroke.Thick, MermaidHead.None, MermaidHead.Arrow, 1)]
    [InlineData("A === B", MermaidStroke.Thick, MermaidHead.None, MermaidHead.None, 1)]
    [InlineData("A --o B", MermaidStroke.Solid, MermaidHead.None, MermaidHead.Circle, 1)]
    [InlineData("A --x B", MermaidStroke.Solid, MermaidHead.None, MermaidHead.Cross, 1)]
    [InlineData("A <--> B", MermaidStroke.Solid, MermaidHead.Arrow, MermaidHead.Arrow, 1)]
    [InlineData("A o--o B", MermaidStroke.Solid, MermaidHead.Circle, MermaidHead.Circle, 1)]
    [InlineData("A ~~~ B", MermaidStroke.Invisible, MermaidHead.None, MermaidHead.None, 1)]
    public void Reads_every_link_style(string link, MermaidStroke stroke, MermaidHead start, MermaidHead end, int length)
    {
        var e = Assert.Single(Flow("graph TD\n" + link).Edges);
        Assert.Equal(("A", "B"), (e.From, e.To));
        Assert.Equal(stroke, e.Stroke);
        Assert.Equal(start, e.StartHead);
        Assert.Equal(end, e.EndHead);
        Assert.Equal(length, e.Length);
    }

    [Theory]
    [InlineData("A -->|yes| B", MermaidStroke.Solid)]
    [InlineData("A-->|yes|B", MermaidStroke.Solid)]
    [InlineData("A -- yes --> B", MermaidStroke.Solid)]
    [InlineData("A -. yes .-> B", MermaidStroke.Dotted)]
    [InlineData("A == yes ==> B", MermaidStroke.Thick)]
    [InlineData("A -->|\"yes\"| B", MermaidStroke.Solid)]
    public void Reads_link_labels_both_ways(string link, MermaidStroke stroke)
    {
        var e = Assert.Single(Flow("graph TD\n" + link).Edges);
        Assert.Equal("yes", e.Label);
        Assert.Equal(stroke, e.Stroke);
        Assert.Equal(MermaidHead.Arrow, e.EndHead);
        Assert.Equal(["A", "B"], Flow("graph TD\n" + link).Nodes.Select(n => n.Id));
    }

    [Fact]
    public void Chains_and_ampersand_groups_expand_into_every_edge()
    {
        var chain = Flow("graph TD\nA --> B -.-> C");
        Assert.Equal([("A", "B"), ("B", "C")], chain.Edges.Select(e => (e.From, e.To)));
        Assert.Equal(MermaidStroke.Dotted, chain.Edges[1].Stroke);

        var groups = Flow("graph TD\nA & B --> C & D");
        Assert.Equal([("A", "C"), ("A", "D"), ("B", "C"), ("B", "D")], groups.Edges.Select(e => (e.From, e.To)));
    }

    [Fact]
    public void An_unreadable_remainder_keeps_what_parsed()
    {
        var fc = Flow("graph TD\nA --> B ?? junk\nC --> D");
        Assert.Contains(fc.Edges, e => e.From == "A" && e.To == "B");
        Assert.Contains(fc.Edges, e => e.From == "C" && e.To == "D");
    }

    // ── Parsing: subgraphs ──

    [Fact]
    public void Subgraphs_collect_their_nodes_and_nest()
    {
        var fc = Flow("""
            flowchart TB
              c1-->a2
              subgraph one [First one]
                a1-->a2
                subgraph inner
                  x1
                end
              end
              subgraph "Quoted title"
                b1
              end
              subgraph two
                b2
              end
            """);
        var one = fc.Subgraphs.Single(s => s.Id == "one");
        Assert.Equal("First one", one.Title);
        var inner = fc.Subgraphs.Single(s => s.Id == "inner");
        Assert.Same(one, inner.Parent);
        Assert.Equal(1, inner.Depth);
        Assert.Contains(fc.Subgraphs, s => s.Title == "Quoted title");
        Assert.Equal("two", fc.Subgraphs.Single(s => s.Id == "two").Title);

        // a2 was first seen outside, then mentioned inside `one`: it joins `one`. c1 is never inside a subgraph.
        Assert.Same(one, fc.Find("a2")!.Subgraph);
        Assert.Same(one, fc.Find("a1")!.Subgraph);
        Assert.Same(inner, fc.Find("x1")!.Subgraph);
        Assert.Null(fc.Find("c1")!.Subgraph);
    }

    [Fact]
    public void An_edge_to_a_subgraph_does_not_invent_a_node()
    {
        var fc = Flow("graph LR\nA --> grp\nsubgraph grp\n  B\nend");
        Assert.Equal(["A", "B"], fc.Nodes.Select(n => n.Id));
        Assert.Equal("grp", Assert.Single(fc.Edges).To);
    }

    // ── Parsing: sequence diagrams ──

    [Fact]
    public void Sequence_participants_aliases_and_actors()
    {
        var seq = Seq("sequenceDiagram\nparticipant A as Alice\nactor B as Bob\nA->>C: hi");
        Assert.Equal(["A", "B", "C"], seq.Participants.Select(p => p.Id));
        Assert.Equal(["Alice", "Bob", "C"], seq.Participants.Select(p => p.Label));
        Assert.Equal([false, true, false], seq.Participants.Select(p => p.IsActor));
    }

    [Theory]
    [InlineData("A->>B: m", MermaidStroke.Solid, MermaidHead.Arrow, false)]
    [InlineData("A-->>B: m", MermaidStroke.Dotted, MermaidHead.Arrow, false)]
    [InlineData("A->B: m", MermaidStroke.Solid, MermaidHead.None, false)]
    [InlineData("A-->B: m", MermaidStroke.Dotted, MermaidHead.None, false)]
    [InlineData("A-xB: m", MermaidStroke.Solid, MermaidHead.Cross, false)]
    [InlineData("A--xB: m", MermaidStroke.Dotted, MermaidHead.Cross, false)]
    [InlineData("A-)B: m", MermaidStroke.Solid, MermaidHead.Open, false)]
    [InlineData("A--)B: m", MermaidStroke.Dotted, MermaidHead.Open, false)]
    [InlineData("A<<->>B: m", MermaidStroke.Solid, MermaidHead.Arrow, true)]
    [InlineData("A<<-->>B: m", MermaidStroke.Dotted, MermaidHead.Arrow, true)]
    public void Sequence_message_arrows(string line, MermaidStroke stroke, MermaidHead head, bool both)
    {
        var m = Assert.IsType<SeqMessage>(Assert.Single(Seq("sequenceDiagram\n" + line).Items));
        Assert.Equal(("A", "B", "m"), (m.From, m.To, m.Text));
        Assert.Equal(stroke, m.Stroke);
        Assert.Equal(head, m.Head);
        Assert.Equal(both, m.Bidirectional);
    }

    [Fact]
    public void Sequence_activations_notes_and_blocks()
    {
        var seq = Seq("""
            sequenceDiagram
              autonumber
              box Purple Group
                participant A
              end
              A->>+B: go
              Note right of B: thinking
              Note over A,B: shared
              loop every minute
                B-->>A: tick
              end
              alt ok
                B-->>-A: done
              else failed
                B-xA: boom
              end
              activate A
              deactivate A
            """);
        Assert.True(seq.AutoNumber);
        var items = seq.Items;
        var go = Assert.IsType<SeqMessage>(items[0]);
        Assert.True(go.Activate);
        Assert.Equal(new SeqNote(SeqNotePlacement.RightOf, "B", null, "thinking"), items[1]);
        Assert.Equal(new SeqNote(SeqNotePlacement.Over, "A", "B", "shared"), items[2]);
        Assert.Equal(new SeqBlockStart("loop", "every minute"), items[3]);
        Assert.IsType<SeqBlockEnd>(items[5]);
        Assert.Equal(new SeqBlockStart("alt", "ok"), items[6]);
        Assert.True(Assert.IsType<SeqMessage>(items[7]).Deactivate);
        Assert.Equal(new SeqBlockDivider("failed"), items[8]);
        Assert.IsType<SeqBlockEnd>(items[10]);
        Assert.Equal(new SeqActivation("A", true), items[11]);
        Assert.Equal(new SeqActivation("A", false), items[12]);
        // The `box … end` grouping draws nothing and must not have closed a block of its own.
        Assert.Equal(2, items.OfType<SeqBlockEnd>().Count());
    }

    // ── Parsing: ER diagrams ──

    private static MermaidEr Er(string src) => Assert.IsType<MermaidEr>(MermaidParser.Parse(src).Diagram);

    [Theory]
    [InlineData("A ||--o{ B : has", MermaidHead.ErExactlyOne, MermaidHead.ErZeroOrMore, true)]
    [InlineData("A |o--|{ B : has", MermaidHead.ErZeroOrOne, MermaidHead.ErOneOrMore, true)]
    [InlineData("A }o..o| B : has", MermaidHead.ErZeroOrMore, MermaidHead.ErZeroOrOne, false)]
    [InlineData("A }|..|| B : has", MermaidHead.ErOneOrMore, MermaidHead.ErExactlyOne, false)]
    [InlineData("A||--o{B:has", MermaidHead.ErExactlyOne, MermaidHead.ErZeroOrMore, true)]
    [InlineData("A only one to zero or more B : has", MermaidHead.ErExactlyOne, MermaidHead.ErZeroOrMore, true)]
    [InlineData("A one or zero optionally to 1+ B : has", MermaidHead.ErZeroOrOne, MermaidHead.ErOneOrMore, false)]
    [InlineData("A 1 to many(0) B : has", MermaidHead.ErExactlyOne, MermaidHead.ErZeroOrMore, true)]
    public void Er_cardinalities_and_line_kinds(string line, MermaidHead atA, MermaidHead atB, bool identifying)
    {
        var r = Assert.Single(Er("erDiagram\n" + line).Relationships);
        Assert.Equal(new ErRelationship("A", "B", atA, atB, identifying, "has"), r);
    }

    [Fact]
    public void Er_labels_names_aliases_and_direction()
    {
        var er = Er("""
            erDiagram
              direction LR
              NAMED-DRIVER }o--|| CAR : "is allowed to drive"
              p[Person] ||--o{ "LONG NAME" : owns
              ORPHAN
            """);
        Assert.Equal(FlowDirection.LeftRight, er.Direction);
        Assert.Equal(["NAMED-DRIVER", "CAR", "p", "LONG NAME", "ORPHAN"], er.Entities.Select(e => e.Id));
        Assert.Equal("Person", er.Entities.Single(e => e.Id == "p").Label);
        Assert.Equal("is allowed to drive", er.Relationships[0].Label);
        Assert.Equal("owns", er.Relationships[1].Label);
    }

    [Fact]
    public void Er_attribute_blocks()
    {
        var er = Er("""
            erDiagram
              CUSTOMER ||--o{ ORDER : places
              CUSTOMER {
                string id PK
                varchar(255) email UK "login name"
                int regionId FK,UK
                string *note
              }
              ORDER {}
            """);
        var c = er.Entities.Single(e => e.Id == "CUSTOMER");
        Assert.Equal(
            [
                new ErAttribute("string", "id", "PK", ""),
                new ErAttribute("varchar(255)", "email", "UK", "login name"),
                new ErAttribute("int", "regionId", "FK, UK", ""),
                new ErAttribute("string", "*note", "", ""),
            ],
            c.Attributes);
        Assert.Empty(er.Entities.Single(e => e.Id == "ORDER").Attributes);
        Assert.Single(er.Relationships);   // the `ORDER {}` line opened and closed nothing else
    }

    // ── Layout: flowcharts ──

    private static MermaidScene Lay(string src) =>
        MermaidLayout.Layout(MermaidParser.Parse(src).Diagram!, Measure)
        ?? throw new Xunit.Sdk.XunitException("layout returned null");

    private static Dictionary<string, MRect> NodeBoxes(MermaidScene scene)
    {
        var shapes = scene.Items.OfType<SceneShape>().Where(s => s.Role == MermaidShapeRole.Node).ToList();
        return scene.Items.OfType<SceneText>().Where(t => t.Role == MermaidTextRole.Node)
            .ToDictionary(t => t.Text, t => shapes.First(s => s.Box.Contains(t.Box.Center)).Box);
    }

    private static bool Overlap(MRect a, MRect b) =>
        a.X < b.Right - 0.01 && b.X < a.Right - 0.01 && a.Y < b.Bottom - 0.01 && b.Y < a.Bottom - 0.01;

    [Theory]
    [InlineData("TD")]
    [InlineData("LR")]
    [InlineData("BT")]
    [InlineData("RL")]
    public void Edges_run_with_the_flow_and_nodes_never_overlap(string dir)
    {
        var scene = Lay("flowchart " + dir + """

              a[Start] --> b{{Decide}}
              b -->|yes| c[Do the long-named thing]
              b -->|no| d([Skip])
              c --> e[(Store)]
              d --> e
              a --> e
              e --> f((End))
            """);
        var boxes = NodeBoxes(scene);
        Assert.Equal(6, boxes.Count);

        var edges = new[] { ("Start", "Decide"), ("Decide", "Do the long-named thing"), ("Decide", "Skip"),
            ("Do the long-named thing", "Store"), ("Skip", "Store"), ("Start", "Store"), ("Store", "End") };
        foreach (var (from, to) in edges)
        {
            var (a, b) = (boxes[from].Center, boxes[to].Center);
            switch (dir)
            {
                case "TD": Assert.True(b.Y > a.Y, $"{from}→{to}"); break;
                case "BT": Assert.True(b.Y < a.Y, $"{from}→{to}"); break;
                case "LR": Assert.True(b.X > a.X, $"{from}→{to}"); break;
                case "RL": Assert.True(b.X < a.X, $"{from}→{to}"); break;
            }
        }

        var all = boxes.Values.ToList();
        for (int i = 0; i < all.Count; i++)
            for (int j = i + 1; j < all.Count; j++)
                Assert.False(Overlap(all[i], all[j]), $"{all[i]} overlaps {all[j]}");

        foreach (var it in scene.Items.OfType<SceneShape>())
        {
            Assert.True(it.Box.X >= 0 && it.Box.Y >= 0, "shape starts inside the canvas");
            Assert.True(it.Box.Right <= scene.Width + 0.01 && it.Box.Bottom <= scene.Height + 0.01, "shape ends inside the canvas");
        }
    }

    [Fact]
    public void Edge_labels_sit_between_their_nodes()
    {
        var scene = Lay("graph TD\nA[Top] -->|the label| B[Bottom]");
        var boxes = NodeBoxes(scene);
        var label = scene.Items.OfType<SceneText>().Single(t => t.Role == MermaidTextRole.EdgeLabel);
        Assert.True(label.Box.Y > boxes["Top"].Bottom);
        Assert.True(label.Box.Bottom < boxes["Bottom"].Y);
    }

    [Fact]
    public void Edges_start_and_end_on_the_node_outline()
    {
        var scene = Lay("graph TD\nA[Top] --> B[Bottom]");
        var boxes = NodeBoxes(scene);
        var line = scene.Items.OfType<SceneLine>().Single();
        Assert.Equal(boxes["Top"].Bottom, line.Points[0].Y, 3);
        Assert.Equal(boxes["Bottom"].Y, line.Points[^1].Y, 3);
    }

    [Fact]
    public void A_cycle_still_lays_out_and_draws_every_edge_in_its_written_direction()
    {
        var scene = Lay("graph TD\nA --> B\nB --> C\nC --> A\nA --> A");
        var boxes = NodeBoxes(scene);
        Assert.Equal(3, boxes.Count);
        var lines = scene.Items.OfType<SceneLine>().ToList();
        Assert.Equal(4, lines.Count);   // three cycle edges plus the self-loop
        // The back edge C → A ends at A (its head end), not at C.
        var back = lines.Single(l => boxes["C"].Inflate(1, 1, 1, 1).Contains(l.Points[0])
                                     && boxes["A"].Inflate(1, 1, 1, 1).Contains(l.Points[^1]));
        Assert.Equal(MermaidHead.Arrow, back.EndHead);
    }

    [Fact]
    public void Subgraph_boxes_hold_their_members_and_keep_strangers_out()
    {
        var scene = Lay("""
            graph TD
              x[Outside] --> a1
              subgraph G [Group]
                a1[One] --> a2[Two]
              end
              x --> y[Also outside]
              a2 --> z[Below]
            """);
        var boxes = NodeBoxes(scene);
        var cluster = scene.Items.OfType<SceneShape>().Single(s => s.Role == MermaidShapeRole.Cluster).Box;
        Assert.True(cluster.Contains(boxes["One"].Center) && cluster.Contains(boxes["Two"].Center));
        foreach (var outside in new[] { "Outside", "Also outside", "Below" })
            Assert.False(Overlap(cluster, boxes[outside]), $"{outside} is inside the subgraph box");
        var title = scene.Items.OfType<SceneText>().Single(t => t.Role == MermaidTextRole.ClusterTitle);
        Assert.True(title.Box.Bottom <= boxes["One"].Y, "the title sits above the first member");
    }

    [Fact]
    public void An_oversized_chart_is_refused_rather_than_laid_out()
    {
        var src = "graph TD\n" + string.Join("\n", Enumerable.Range(0, 450).Select(i => $"n{i} --> n{i + 1}"));
        Assert.Null(MermaidLayout.Layout(MermaidParser.Parse(src).Diagram!, Measure));
    }

    // ── Layout: ER diagrams ──

    [Fact]
    public void Er_entities_hold_their_attributes_and_relationships_carry_their_markers()
    {
        var scene = Lay("""
            erDiagram
              CUSTOMER ||--o{ ORDER : places
              ORDER }o..|| CUSTOMER : "billed to"
              ORDER }o--o| ORDER : "follows"
              CUSTOMER {
                string id PK
                string name
              }
            """);
        var entities = scene.Items.OfType<SceneText>().Where(t => t.Role == MermaidTextRole.ErEntity)
            .ToDictionary(t => t.Text, t => scene.Items.OfType<SceneShape>()
                .First(s => s.Role == MermaidShapeRole.Node && s.Box.Contains(t.Box.Center)).Box);
        Assert.Equal(["CUSTOMER", "ORDER"], entities.Keys.Order());
        Assert.False(Overlap(entities["CUSTOMER"], entities["ORDER"]));

        // Attribute cells sit inside their entity, below its header band.
        var header = scene.Items.OfType<SceneShape>().Single(s => s.Role == MermaidShapeRole.ErHeader).Box;
        foreach (var cell in scene.Items.OfType<SceneText>().Where(t => t.Text is "id" or "name" or "PK" or "string"))
        {
            Assert.True(entities["CUSTOMER"].Contains(cell.Box.Center), $"{cell.Text} is inside CUSTOMER");
            Assert.True(cell.Box.Y >= header.Bottom - 0.01, $"{cell.Text} is below the header");
        }

        var lines = scene.Items.OfType<SceneLine>().ToList();
        Assert.Equal(3, lines.Count);
        var places = lines.Single(l => l.StartHead == MermaidHead.ErExactlyOne && l.EndHead == MermaidHead.ErZeroOrMore);
        Assert.Equal(MermaidStroke.Solid, places.Stroke);
        Assert.Equal(MermaidStroke.Dotted, lines.Single(l => l.EndHead == MermaidHead.ErExactlyOne).Stroke);
        var loop = lines.Single(l => l.StartHead == MermaidHead.ErZeroOrMore && l.EndHead == MermaidHead.ErZeroOrOne);
        Assert.True(loop.Curved);
        Assert.Equal(3, scene.Items.OfType<SceneText>().Count(t => t.Role == MermaidTextRole.EdgeLabel));
    }

    // ── Layout: sequence diagrams ──

    [Fact]
    public void Sequence_columns_follow_declaration_and_rows_follow_time()
    {
        var scene = Lay("""
            sequenceDiagram
              participant A as Alpha
              participant B as Beta
              participant C as Gamma
              A->>B: first message
              loop again
                B->>C: a rather long second message that needs room
              end
              C-->>A: third
            """);
        var heads = scene.Items.OfType<SceneText>().Where(t => t.Role == MermaidTextRole.Actor)
            .GroupBy(t => t.Text).ToDictionary(g => g.Key, g => g.Min(t => t.Box.CenterX));
        Assert.True(heads["Alpha"] < heads["Beta"] && heads["Beta"] < heads["Gamma"]);

        var messages = scene.Items.OfType<SceneLine>().Where(l => l.Role == MermaidLineRole.Message).ToList();
        Assert.Equal(3, messages.Count);
        Assert.True(messages[0].Points[0].Y < messages[1].Points[0].Y && messages[1].Points[0].Y < messages[2].Points[0].Y);

        // The long label fits between Beta and Gamma.
        var label = scene.Items.OfType<SceneText>().Single(t => t.Text.StartsWith("a rather long", StringComparison.Ordinal));
        Assert.True(label.Box.W < heads["Gamma"] - heads["Beta"]);

        // The loop frames the message inside it.
        var frame = scene.Items.OfType<SceneShape>().Single(s => s.Role == MermaidShapeRole.Block).Box;
        Assert.True(frame.Contains(messages[1].Points[0]) && frame.Contains(messages[1].Points[^1]));
        Assert.False(frame.Contains(messages[0].Points[0]));
    }

    [Fact]
    public void Sequence_activations_draw_bars()
    {
        var scene = Lay("sequenceDiagram\nA->>+B: start\nB-->>-A: stop");
        var bar = scene.Items.OfType<SceneShape>().Single(s => s.Role == MermaidShapeRole.Activation);
        Assert.True(bar.Box.H > 0);
    }

    // ── Fullscreen viewer: pan and zoom ──

    private static MPoint ToScreen(PanZoom v, MPoint c) => new(c.X * v.Zoom + v.X, c.Y * v.Zoom + v.Y);

    [Fact]
    public void Zooming_keeps_the_point_under_the_cursor_still()
    {
        var view = new PanZoom(1.5, -40, 25);
        var cursor = new MPoint(300, 180);
        var content = new MPoint((cursor.X - view.X) / view.Zoom, (cursor.Y - view.Y) / view.Zoom);   // what's under it
        foreach (var factor in new[] { 1.2, 1 / 1.2, 3, 0.25 })
        {
            var after = ToScreen(view.ZoomAt(cursor, factor), content);
            Assert.Equal(cursor.X, after.X, 6);
            Assert.Equal(cursor.Y, after.Y, 6);
        }
    }

    [Fact]
    public void Zoom_stays_within_its_limits()
    {
        Assert.Equal(PanZoom.MaxZoom, new PanZoom(4, 0, 0).ZoomAt(new MPoint(0, 0), 100).Zoom);
        Assert.Equal(PanZoom.MinZoom, new PanZoom(0.5, 0, 0).ZoomAt(new MPoint(0, 0), 0.001).Zoom);
    }

    [Fact]
    public void Fit_centres_the_whole_diagram_and_caps_small_ones()
    {
        var big = PanZoom.Fit(new MSize(2000, 1000), new MSize(1000, 800), 20, 2);
        Assert.Equal((1000 - 40) / 2000.0, big.Zoom, 6);   // bound by width
        var topLeft = ToScreen(big, new MPoint(0, 0));
        var bottomRight = ToScreen(big, new MPoint(2000, 1000));
        Assert.Equal(1000 - bottomRight.X, topLeft.X, 6);   // centred horizontally
        Assert.Equal(800 - bottomRight.Y, topLeft.Y, 6);    // and vertically

        var small = PanZoom.Fit(new MSize(100, 50), new MSize(1000, 800), 20, 2);
        Assert.Equal(2, small.Zoom);   // would fit at 9.6×, opens at the cap
    }

    [Fact]
    public void Clamp_keeps_part_of_the_diagram_on_screen()
    {
        var content = new MSize(500, 400);
        var viewport = new MSize(800, 600);
        var flung = new PanZoom(1, -5000, 9000).Clamp(content, viewport, 48);
        Assert.Equal(48 - 500, flung.X);    // its right edge still 48px into the view
        Assert.Equal(600 - 48, flung.Y);    // its top edge still 48px above the bottom
        var fine = new PanZoom(1, 100, 50);
        Assert.Equal(fine, fine.Clamp(content, viewport, 48));
    }

    // ── Geometry ──

    [Fact]
    public void Clipping_lands_on_each_outline()
    {
        var box = new MRect(0, 0, 100, 40);
        Assert.Equal(new MPoint(50, 40), MermaidGeometry.Clip(FlowShape.Rect, box, new MPoint(50, 200)));
        var circle = MermaidGeometry.Clip(FlowShape.Circle, new MRect(0, 0, 40, 40), new MPoint(20, 200));
        Assert.Equal(40, circle.Y, 6);
        // A diamond's outline at 45° from a 100×40 box's centre: on the line x/50 + y/20 = 1.
        var d = MermaidGeometry.Clip(FlowShape.Rhombus, box, new MPoint(150, 120));
        Assert.Equal(1, (d.X - 50) / 50 + (d.Y - 20) / 20, 6);
    }
}

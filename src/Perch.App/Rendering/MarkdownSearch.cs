using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;

namespace Perch.Avalonia.Rendering;

/// <summary>
/// Shared find-in-page engine for the Markdown window's two panes (rendered preview + source editor). Holds
/// the query, the ordered match list and the current index, plus the navigation/repaint bookkeeping; the
/// concrete subclasses supply the searchable text (as ordered "segments"), the repaint hook, and the
/// scroll-to-current behaviour for their particular surface. Matching is case-insensitive.
///
/// Both surfaces paint their matches through an owner-drawn overlay (mirroring <c>DiffView</c>'s highlight
/// layer): every match gets a subtle translucent wash and the current one a stronger wash plus an outline,
/// located through the underlying <see cref="TextLayout"/>. The layer sits <em>above</em> the text (not
/// behind) so it also shows over the opaque backgrounds Markdown code/table blocks and the editor card paint;
/// the fills stay translucent so the text keeps reading.
/// </summary>
internal abstract class FindHighlighter
{
    /// <summary>A match: an index into the surface's ordered segments plus the char range within it.</summary>
    protected readonly record struct Match(int Seg, int Start, int Len);

    protected readonly List<Match> Matches = new();
    protected int Index = -1;
    private string _query = "";

    // Match fill + the current-match outline. Translucent (the layer paints over the text) so glyphs stay
    // legible; the current match adds an opaque outline so it reads as "current" without a heavier wash.
    protected IBrush MatchFill = null!;
    protected IBrush CurrentFill = null!;
    protected IPen CurrentPen = null!;

    /// <summary>Raised after the match set changes — (current 1-based index or 0 when none, total).</summary>
    public event Action<int, int>? ResultsChanged;

    protected FindHighlighter() => SetBrushes(dark: true);

    // Keyed to the surface's own light/dark polarity so the wash reads on either paper.
    protected void SetBrushes(bool dark)
    {
        IBrush A(byte a, byte r, byte g, byte b) => new SolidColorBrush(Color.FromArgb(a, r, g, b));
        if (dark)
        {
            MatchFill = A(72, 250, 204, 21);
            CurrentFill = A(80, 255, 158, 40);
            CurrentPen = new Pen(new SolidColorBrush(Color.FromRgb(255, 158, 40)), 1.5);
        }
        else
        {
            MatchFill = A(90, 245, 208, 0);
            CurrentFill = A(80, 240, 138, 0);
            CurrentPen = new Pen(new SolidColorBrush(Color.FromRgb(240, 138, 0)), 1.5);
        }
    }

    /// <summary>Set the query, jump to the first match, and repaint. An empty query clears the search.</summary>
    public void SetSearch(string query)
    {
        _query = query ?? "";
        Index = 0;
        Recompute();
        Highlight(scroll: true);
        Raise();
    }

    /// <summary>Move to the next match (wraps).</summary>
    public void Next() => Move(1);

    /// <summary>Move to the previous match (wraps).</summary>
    public void Prev() => Move(-1);

    /// <summary>Drop the query and every highlight.</summary>
    public void Clear()
    {
        _query = "";
        Matches.Clear();
        Index = -1;
        Repaint();
        Raise();
    }

    /// <summary>Recompute matches against the (possibly changed) segments and repaint without scrolling — for
    /// a surface rebuilt underneath us while find is open (a preview re-render, or an edit to the source).</summary>
    public void Refresh()
    {
        if (_query.Length == 0)
        {
            Matches.Clear();
            Index = -1;
        }
        else
        {
            Recompute();
        }
        Repaint();
        Raise();
    }

    private void Move(int delta)
    {
        if (Matches.Count > 0)
        {
            Index = ((Index + delta) % Matches.Count + Matches.Count) % Matches.Count;
            Highlight(scroll: true);
        }
        Raise();
    }

    private void Recompute()
    {
        Matches.Clear();
        if (_query.Length > 0)
        {
            var segments = Segments();
            for (int s = 0; s < segments.Count; s++)
            {
                if (!SegmentVisible(s))
                    continue;
                var text = segments[s];
                int i = 0;
                while ((i = text.IndexOf(_query, i, StringComparison.OrdinalIgnoreCase)) >= 0)
                {
                    Matches.Add(new Match(s, i, _query.Length));
                    i += _query.Length;
                }
            }
        }

        if (Matches.Count == 0) Index = -1;
        else if (Index < 0) Index = 0;
        else if (Index >= Matches.Count) Index = Matches.Count - 1;
    }

    private void Highlight(bool scroll)
    {
        Repaint();
        if (scroll && Index >= 0 && Index < Matches.Count)
        {
            var m = Matches[Index];
            Dispatcher.UIThread.Post(() => ScrollToCurrent(m), DispatcherPriority.Background);
        }
    }

    private void Raise() => ResultsChanged?.Invoke(Matches.Count == 0 ? 0 : Index + 1, Matches.Count);

    /// <summary>The ordered searchable text units of the surface (a preview's blocks, or the editor's one body).</summary>
    protected abstract IReadOnlyList<string> Segments();

    /// <summary>Whether segment <paramref name="seg"/> is currently on-screen (e.g. not a collapsed block).</summary>
    protected virtual bool SegmentVisible(int seg) => true;

    /// <summary>Invalidate the surface's highlight layer.</summary>
    protected abstract void Repaint();

    /// <summary>Scroll the surface so the current match sits at the centre of its viewport.</summary>
    protected abstract void ScrollToCurrent(Match m);
}

/// <summary>
/// Find-in-page over the rendered Markdown preview. The preview tree is rebuilt on every edit/theme change,
/// so the owner calls <see cref="SetContent"/> after each build to re-collect the searchable blocks (in
/// document order) and re-run the active query without yanking the scroll. Match offsets index each block's
/// flattened text, with every inline UI element (a task-list checkbox) counted as one position so they line
/// up with the block's text layout.
/// </summary>
internal sealed class MarkdownSearch : FindHighlighter
{
    private readonly ScrollViewer _scroll;
    private readonly List<SelectableTextBlock> _blocks = new();
    private readonly List<string> _texts = new();
    private Layer? _layer;

    public MarkdownSearch(ScrollViewer scroll) => _scroll = scroll;

    /// <summary>
    /// Wrap a freshly-built preview <paramref name="root"/> with the highlight layer, re-collect its
    /// searchable text blocks, and re-run the active query (repaint only — no scroll jump). Returns the
    /// control the caller sets as the scroll's content.
    /// </summary>
    public Control SetContent(Control root, bool previewDark)
    {
        SetBrushes(previewDark);
        _blocks.Clear();
        _texts.Clear();
        Collect(root);

        _layer = new Layer(this);
        // Layer on top of the content (translucent) so it shows over opaque code/table backgrounds.
        var grid = new Grid { Children = { root, _layer } };

        Refresh();
        return grid;
    }

    protected override IReadOnlyList<string> Segments() => _texts;

    protected override bool SegmentVisible(int seg) => _blocks[seg].IsEffectivelyVisible;

    protected override void Repaint() => _layer?.InvalidateVisual();

    protected override void ScrollToCurrent(Match m)
    {
        if (_scroll.Content is not Control content || m.Seg >= _blocks.Count)
            return;
        var tb = _blocks[m.Seg];
        if (tb.TextLayout is not { } layout)
            return;
        var rect = layout.HitTestTextRange(m.Start, m.Len).FirstOrDefault();
        var local = new Point(tb.Padding.Left + rect.X, tb.Padding.Top + rect.Y);
        if (tb.TranslatePoint(local, content) is not { } p)
        {
            tb.BringIntoView();
            return;
        }
        double max = Math.Max(0, _scroll.Extent.Height - _scroll.Viewport.Height);
        double y = Math.Clamp(p.Y - _scroll.Viewport.Height / 2, 0, max);
        _scroll.Offset = new Vector(_scroll.Offset.X, y);
    }

    // Locate each match through its block's text layout and fill it — subtle for all, stronger + outlined for
    // the current one.
    private void Paint(DrawingContext ctx, Control layer)
    {
        for (int i = 0; i < Matches.Count; i++)
        {
            var m = Matches[i];
            var tb = _blocks[m.Seg];
            if (!tb.IsEffectivelyVisible || tb.TextLayout is not { } layout)
                continue;
            if (tb.TranslatePoint(new Point(tb.Padding.Left, tb.Padding.Top), layer) is not { } origin)
                continue;
            bool current = i == Index;
            foreach (var r in layout.HitTestTextRange(m.Start, m.Len))
            {
                var box = new Rect(origin.X + r.X, origin.Y + r.Y, r.Width, r.Height);
                ctx.FillRectangle(current ? CurrentFill : MatchFill, box);
                if (current)
                    ctx.DrawRectangle(null, CurrentPen, box);
            }
        }
    }

    // Depth-first walk of the logical tree collecting the selectable text blocks in document order. Recursion
    // stops at a SelectableTextBlock (its inline runs are its logical children — not blocks to search into).
    private void Collect(ILogical node)
    {
        foreach (var child in node.LogicalChildren)
        {
            if (child is SelectableTextBlock t)
            {
                _blocks.Add(t);
                _texts.Add(Flatten(t));
            }
            else
            {
                Collect(child);
            }
        }
    }

    // The block's text as the layout indexes it: run text concatenated, with each inline UI element (a task
    // checkbox) counted as one position so match offsets line up with HitTestTextRange.
    private static string Flatten(SelectableTextBlock t)
    {
        if (t.Inlines is not { Count: > 0 } inlines)
            return t.Text ?? "";
        var sb = new StringBuilder();
        Flatten(inlines, sb);
        return sb.ToString();
    }

    private static void Flatten(InlineCollection inlines, StringBuilder sb)
    {
        foreach (var inline in inlines)
            switch (inline)
            {
                case Run r: sb.Append(r.Text); break;
                case LineBreak: sb.Append('\n'); break;
                case InlineUIContainer: sb.Append('￼'); break;   // one layout position (object replacement)
                case Span s: Flatten(s.Inlines, sb); break;
            }
    }

    // The transparent overlay that paints the match rectangles. Non-hit-testable so clicks fall through to
    // the preview text (the preview→source cursor sync).
    private sealed class Layer : Control
    {
        private readonly MarkdownSearch _owner;
        public Layer(MarkdownSearch owner) { _owner = owner; IsHitTestVisible = false; }
        public override void Render(DrawingContext context) => _owner.Paint(context, this);
    }
}

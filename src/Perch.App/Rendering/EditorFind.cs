using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Media;

namespace Perch.Avalonia.Rendering;

/// <summary>
/// Find-in-page over the Markdown window's source editor (a <c>TextBox</c>). Avalonia's TextBox can't tint a
/// substring, so — like the editor's syntax highlighting and gutter — matches are painted by an owner-drawn
/// <see cref="Layer"/> laid over the editor card: each match is located through the editor's own
/// <see cref="TextPresenter.TextLayout"/> (<c>HitTestTextRange</c>) and offset by the box's padding minus the
/// inner scroll, exactly as <c>MarkdownWindow.UpdateGutter</c> places the gutter bar, so the highlight tracks
/// wrapping and scrolling. The presenter and inner scroll are resolved lazily by the owner (they come from the
/// TextBox template), so this takes accessors rather than the parts themselves.
/// </summary>
internal sealed class EditorFind : FindHighlighter
{
    private readonly TextBox _box;
    private readonly Func<TextPresenter?> _presenter;
    private readonly Func<ScrollViewer?> _scroll;
    private readonly string[] _segment = new string[1];

    /// <summary>The overlay the owner adds over the editor card (above the text, below the gutter bar).</summary>
    public Control Layer { get; }

    public EditorFind(TextBox box, Func<TextPresenter?> presenter, Func<ScrollViewer?> scroll)
    {
        _box = box;
        _presenter = presenter;
        _scroll = scroll;
        Layer = new HighlightLayer(this);
    }

    /// <summary>Recolour the highlights for the editor's current light/dark polarity and repaint.</summary>
    public void SetDark(bool dark) { SetBrushes(dark); Repaint(); }

    /// <summary>Repaint on an editor scroll / re-wrap so the highlights follow the text.</summary>
    public void OnEditorMoved() => Repaint();

    protected override IReadOnlyList<string> Segments()
    {
        _segment[0] = _box.Text ?? "";
        return _segment;
    }

    protected override void Repaint() => Layer.InvalidateVisual();

    protected override void ScrollToCurrent(Match m)
    {
        if (_presenter() is not { TextLayout: { } layout } || _scroll() is not { } scroll)
            return;
        var rect = layout.HitTestTextRange(m.Start, m.Len).FirstOrDefault();
        double target = _box.Padding.Top + rect.Y - scroll.Viewport.Height / 2;
        target = Math.Clamp(target, 0, Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height));
        scroll.Offset = new Vector(scroll.Offset.X, target);
    }

    // Locate each match through the editor's text layout, offset by padding minus the inner scroll (matching
    // the gutter), and fill it — subtle for all, stronger + outlined for the current one.
    private void Paint(DrawingContext ctx)
    {
        if (_presenter() is not { TextLayout: { } layout })
            return;
        var scroll = _scroll();
        double sx = scroll?.Offset.X ?? 0, sy = scroll?.Offset.Y ?? 0;
        double px = _box.Padding.Left, py = _box.Padding.Top;
        for (int i = 0; i < Matches.Count; i++)
        {
            var m = Matches[i];
            bool current = i == Index;
            foreach (var r in layout.HitTestTextRange(m.Start, m.Len))
            {
                var box = new Rect(px + r.X - sx, py + r.Y - sy, r.Width, r.Height);
                ctx.FillRectangle(current ? CurrentFill : MatchFill, box);
                if (current)
                    ctx.DrawRectangle(null, CurrentPen, box);
            }
        }
    }

    private sealed class HighlightLayer : Control
    {
        private readonly EditorFind _owner;
        public HighlightLayer(EditorFind owner) { _owner = owner; IsHitTestVisible = false; }
        public override void Render(DrawingContext context) => _owner.Paint(context);
    }
}

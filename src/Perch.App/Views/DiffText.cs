using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Perch.Data;

namespace Perch.Avalonia.Views;

/// <summary>
/// A unified diff as one selectable text block, with each line's <c>+</c>/<c>-</c> marker painted in a gutter and its
/// changed-line band painted behind it. Built as a row of controls per line, a 50-line edit was about 200 controls
/// and most of an Edit card's build cost (perf review PF17). Selecting copies the code without the markers, across
/// lines. Wrapped lines keep one marker and one band spanning every visual row they take.
/// </summary>
internal sealed class DiffText : Control
{
    private const double Gutter = 14;

    private readonly SelectableTextBlock _text;
    private readonly Line[] _lines;
    private readonly IBrush _addFg, _remFg, _addBand, _remBand;
    private readonly FontFamily _mono;
    private readonly double _size;
    private FormattedText? _plus, _minus;

    // Each line's character range in the block's text, kept so its rows can be found in the laid-out text.
    private readonly record struct Line(int Start, int Length, GitDiffLineKind Kind);

    // The band and marker rows of the layout they were measured from; re-derived when the text is laid out again.
    private TextLayout? _bandsFor;
    private readonly List<(Rect Band, GitDiffLineKind Kind)> _bands = new();

    public DiffText(IReadOnlyList<GitDiffLine> lines, FontFamily mono, double size, double lineHeight,
        IBrush addFg, IBrush remFg, IBrush addBand, IBrush remBand, IBrush contextFg, IBrush metaFg)
    {
        _mono = mono;
        _size = size;
        _addFg = addFg;
        _remFg = remFg;
        _addBand = addBand;
        _remBand = remBand;

        var inlines = new InlineCollection();
        _lines = new Line[lines.Count];
        int offset = 0;
        for (int i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            // An empty line still needs a glyph to give it a row; the newline is part of the line's range so a blank
            // line's band has something to measure.
            string text = (l.Text.Length == 0 ? " " : l.Text) + (i < lines.Count - 1 ? "\n" : "");
            var fg = l.Kind switch
            {
                GitDiffLineKind.Added   => addFg,
                GitDiffLineKind.Removed => remFg,
                GitDiffLineKind.Meta    => metaFg,
                _                       => contextFg,
            };
            inlines.Add(new Run(text) { Foreground = fg });
            _lines[i] = new Line(offset, text.Length, l.Kind);
            offset += text.Length;
        }

        _text = new SelectableTextBlock
        {
            FontFamily = mono, FontSize = size, LineHeight = lineHeight, TextWrapping = TextWrapping.Wrap,
            Inlines = inlines,
        };
        LogicalChildren.Add(_text);
        VisualChildren.Add(_text);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _text.Measure(availableSize.Deflate(new Thickness(Gutter, 0, 0, 0)));
        return new Size(_text.DesiredSize.Width + Gutter, _text.DesiredSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _text.Arrange(new Rect(Gutter, 0, Math.Max(0, finalSize.Width - Gutter), finalSize.Height));
        InvalidateVisual();   // the bands follow the text's new layout
        return finalSize;
    }

    public override void Render(DrawingContext context)
    {
        var layout = _text.TextLayout;
        if (!ReferenceEquals(layout, _bandsFor)) MeasureBands(layout);

        double width = Bounds.Width;
        foreach (var (band, kind) in _bands)
        {
            var (fill, marker) = kind == GitDiffLineKind.Added ? (_addBand, _plus ??= Marker("+", _addFg))
                                                                : (_remBand, _minus ??= Marker("-", _remFg));
            context.FillRectangle(fill, new Rect(0, band.Top, width, band.Height));
            context.DrawText(marker, new Point(0, band.Top + (_text.LineHeight - marker.Height) / 2));
        }
    }

    // Finds the rows each changed line occupies in the laid-out text. Done once per layout, not per paint.
    private void MeasureBands(TextLayout layout)
    {
        _bandsFor = layout;
        _bands.Clear();
        foreach (var line in _lines)
        {
            if (line.Kind is not (GitDiffLineKind.Added or GitDiffLineKind.Removed)) continue;
            double top = double.PositiveInfinity, bottom = double.NegativeInfinity;
            foreach (var r in layout.HitTestTextRange(line.Start, line.Length))
            {
                top = Math.Min(top, r.Top);
                bottom = Math.Max(bottom, r.Bottom);
            }
            if (bottom > top) _bands.Add((new Rect(0, top, 0, bottom - top), line.Kind));
        }
    }

    private FormattedText Marker(string text, IBrush brush) =>
        new(text, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(_mono), _size, brush);
}

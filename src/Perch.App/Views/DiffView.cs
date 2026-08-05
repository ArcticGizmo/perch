using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Perch.Avalonia.Rendering;
using Perch.Avalonia.Theming;
using Perch.Data;

namespace Perch.Avalonia.Views;

/// <summary>
/// The owner-drawn unified-diff surface for the Change Review window — a plain (no syntax-highlighting)
/// diff renderer that mirrors <see cref="StatsDashboard"/>'s single measure-or-paint routine:
/// <see cref="Draw"/> advances a shared <c>y</c> on both passes and paints only when the context is
/// non-null, so the measured height (via <see cref="MeasureOverride"/>) and the painted layout can never
/// drift. Hosted in a <c>ScrollViewer</c>; horizontal overflow is clipped (<see cref="ClipToBounds"/>),
/// there's no horizontal scroll in M1.
///
/// Diff body lines are drawn in a monospace face built here directly — <see cref="OverlayDraw.Text"/> is
/// Inter-only, which wouldn't align a diff. Added lines are <see cref="Palette.Green"/>, removed
/// <see cref="Palette.Red"/>, context muted; each row's height comes from the measured font line height,
/// never a constant (the CLAUDE.md anti-clip rule).
/// </summary>
internal sealed class DiffView : Control
{
    private static readonly Color BodyBg = Color.FromRgb(18, 18, 24);
    private static readonly IBrush FileBarBg = new SolidColorBrush(Color.FromRgb(30, 30, 42));
    private static readonly IBrush TitleBrush = new SolidColorBrush(Palette.Title);
    private static readonly IBrush MutedBrush = new SolidColorBrush(Palette.Muted);
    private static readonly IBrush ContextBrush = new SolidColorBrush(Color.FromRgb(170, 170, 188));
    private static readonly IBrush AddedBrush = new SolidColorBrush(Palette.Green);
    private static readonly IBrush RemovedBrush = new SolidColorBrush(Palette.Red);
    private static readonly IBrush HunkBrush = new SolidColorBrush(Palette.Accent);
    private static readonly IBrush AddedRowBg = new SolidColorBrush(Color.FromArgb(38, 34, 197, 94));
    private static readonly IBrush RemovedRowBg = new SolidColorBrush(Color.FromArgb(38, 239, 68, 68));
    private static readonly IPen BorderPen = new Pen(new SolidColorBrush(Palette.Border), 1);

    private static readonly Typeface Mono =
        new(new FontFamily("Cascadia Code, Consolas, Menlo, monospace"));

    private const double Pad = 16, LineSize = 12.5, PathSize = 13, HunkSize = 12, RowPadX = 8;

    // The monospace line height is the font's, measured once — every row advances by it so glyphs never
    // clip and empty lines still occupy a full row (anti-clip rule; a bare "" can measure zero-height).
    private static readonly double LineH = MonoText("Mg", LineSize, MutedBrush).Height;

    private GitDiff? _diff;
    private string? _note = "Select a file or commit to see its diff.";
    private bool _loading;

    public DiffView()
    {
        ClipToBounds = true; // long lines clip at the right edge — no horizontal scroll in M1
    }

    public void SetLoading()
    {
        _loading = true;
        _diff = null;
        Invalidate();
    }

    /// <summary>Show a diff, or clear it to a placeholder <paramref name="note"/> (e.g. "Binary file",
    /// "Nothing selected"). A non-null <paramref name="diff"/> with no files also falls back to the note.</summary>
    public void SetDiff(GitDiff? diff, string? note)
    {
        _loading = false;
        _diff = diff;
        _note = note;
        Invalidate();
    }

    private void Invalidate()
    {
        InvalidateMeasure();
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double w = double.IsFinite(availableSize.Width) && availableSize.Width > 0 ? availableSize.Width : 720;
        return new Size(w, Draw(null, w));
    }

    public override void Render(DrawingContext ctx)
    {
        ctx.FillRectangle(new SolidColorBrush(BodyBg), new Rect(Bounds.Size));
        Draw(ctx, Bounds.Width);
    }

    // Single source of layout: advances y on both passes, paints only when ctx != null, returns height.
    private double Draw(DrawingContext? ctx, double width)
    {
        double y = Pad;

        if (_loading)
            return MessageLine(ctx, "Loading…", y);
        if (_diff is not { Files.Count: > 0 })
            return MessageLine(ctx, _note ?? "No changes.", y);

        foreach (var file in _diff.Value.Files)
        {
            y = FileHeader(ctx, file, y, width);

            if (file.IsBinary)
            {
                y = BodyLine(ctx, "Binary file — not shown.", MutedBrush, null, y, width);
                y += LineH * 0.4;
                continue;
            }
            if (file.Hunks.Count == 0)
            {
                y = BodyLine(ctx, "No textual changes (mode/rename only).", MutedBrush, null, y, width);
                y += LineH * 0.4;
                continue;
            }

            foreach (var hunk in file.Hunks)
            {
                y = BodyLine(ctx, hunk.Header, HunkBrush, null, y, width);
                foreach (var line in hunk.Lines)
                {
                    var (brush, bg) = line.Kind switch
                    {
                        GitDiffLineKind.Added => (AddedBrush, AddedRowBg),
                        GitDiffLineKind.Removed => (RemovedBrush, RemovedRowBg),
                        GitDiffLineKind.Meta => (MutedBrush, (IBrush?)null),
                        _ => (ContextBrush, null),
                    };
                    y = BodyLine(ctx, Marker(line.Kind) + line.Text, brush, bg, y, width);
                }
            }
            y += LineH * 0.5; // gap between files
        }

        return y + Pad;
    }

    // A single mono body row: optional full-width background tint, then the (clipped) monospace text.
    private double BodyLine(DrawingContext? ctx, string text, IBrush brush, IBrush? rowBg, double y, double width)
    {
        if (ctx is not null)
        {
            if (rowBg is not null)
                ctx.FillRectangle(rowBg, new Rect(0, y, width, LineH));
            if (text.Length > 0)
                ctx.DrawText(MonoText(text, LineSize, brush), new Point(RowPadX, y));
        }
        return y + LineH;
    }

    // The file separator bar: a full-width tinted strip carrying the path label (old→new for renames,
    // the single path otherwise). Height derived from the measured header text.
    private double FileHeader(DrawingContext? ctx, GitDiffFile file, double y, double width)
    {
        string label = FileLabel(file);
        var ft = OverlayDraw.Text(label, PathSize, TitleBrush, FontWeight.SemiBold);
        double barH = ft.Height + 10;
        if (ctx is not null)
        {
            var r = new Rect(0, y, width, barH);
            ctx.FillRectangle(FileBarBg, r);
            ctx.DrawLine(BorderPen, new Point(0, y + barH), new Point(width, y + barH));
            OverlayDraw.TextLeftMid(ctx, ft, RowPadX, y + barH / 2);
        }
        return y + barH;
    }

    private double MessageLine(DrawingContext? ctx, string text, double y)
    {
        if (ctx is not null)
            ctx.DrawText(OverlayDraw.Text(text, PathSize, MutedBrush), new Point(Pad, y));
        return y + LineH + Pad;
    }

    private static string FileLabel(GitDiffFile file) => (file.OldPath, file.NewPath) switch
    {
        (null, { } n) => $"added: {n}",
        ({ } o, null) => $"deleted: {o}",
        ({ } o, { } n) when o != n => $"{o} → {n}",
        ({ } o, _) => o,
        _ => "(unknown)",
    };

    private static string Marker(GitDiffLineKind kind) => kind switch
    {
        GitDiffLineKind.Added => "+ ",
        GitDiffLineKind.Removed => "- ",
        GitDiffLineKind.Meta => "",
        _ => "  ",
    };

    private static FormattedText MonoText(string s, double size, IBrush brush) =>
        new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Mono, size, brush);
}

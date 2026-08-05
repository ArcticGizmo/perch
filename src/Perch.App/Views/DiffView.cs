using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Perch.Avalonia.Theming;
using Perch.Data;

namespace Perch.Avalonia.Views;

/// <summary>
/// The unified-or-split diff surface for the Change Review window. Built from real text controls
/// (<see cref="SelectableTextBlock"/>) rather than owner-drawn glyphs, so the text is selectable and
/// copyable (Ctrl+C). Each hunk body is a single selectable block, so selection spans the lines within a
/// hunk. Diff lines are monospace; added lines are <see cref="Palette.Green"/>, removed
/// <see cref="Palette.Red"/>, context muted.
///
/// Two layouts, toggled by <see cref="SetSplit"/>: <b>Unified</b> (one column, +/- markers) and
/// <b>Split</b> (side-by-side old|new like GitHub/GitKraken — removed lines pair with the following added
/// lines, blanks pad the shorter side so rows line up). Rebuilds its visual tree on
/// <see cref="SetDiff"/>/<see cref="SetSplit"/>; hosted in a <c>ScrollViewer</c>.
/// </summary>
internal sealed class DiffView : Border
{
    private static readonly Color BodyBg = Color.FromRgb(18, 18, 24);
    private static readonly IBrush FileBarBg = new SolidColorBrush(Color.FromRgb(30, 30, 42));
    private static readonly IBrush TitleBrush = new SolidColorBrush(Palette.Title);
    private static readonly IBrush MutedBrush = new SolidColorBrush(Palette.Muted);
    private static readonly IBrush ContextBrush = new SolidColorBrush(Color.FromRgb(180, 180, 198));
    private static readonly IBrush AddedBrush = new SolidColorBrush(Palette.Green);
    private static readonly IBrush RemovedBrush = new SolidColorBrush(Palette.Red);
    private static readonly IBrush HunkBrush = new SolidColorBrush(Palette.Accent);
    private static readonly IBrush SelectionBrush = new SolidColorBrush(Color.FromArgb(90, 96, 165, 250));

    private static readonly FontFamily Mono = new("Cascadia Code, Consolas, Menlo, monospace");
    private const double LineSize = 12.5, PathSize = 13, HunkSize = 12;

    // Above this many lines, a single file's diff is truncated with a note — an owner-drawn safety valve so
    // a pathological (e.g. generated / minified) file can't spawn tens of thousands of inline runs.
    private const int MaxLinesPerFile = 4000;

    private GitDiff? _diff;
    private string? _note = "Select a file or commit to see its diff.";
    private bool _loading;
    private bool _split;

    public DiffView()
    {
        Background = new SolidColorBrush(BodyBg);
        Padding = new Thickness(0, 0, 0, 12);
        Rebuild();
    }

    public void SetLoading()
    {
        _loading = true;
        _diff = null;
        Rebuild();
    }

    /// <summary>Show a diff, or clear it to a placeholder <paramref name="note"/> (e.g. "Binary file",
    /// nothing selected). A non-null <paramref name="diff"/> with no files also falls back to the note.</summary>
    public void SetDiff(GitDiff? diff, string? note)
    {
        _loading = false;
        _diff = diff;
        _note = note;
        Rebuild();
    }

    /// <summary>Switch between unified (false) and side-by-side split (true) layout, re-rendering the
    /// current diff. A no-op if the mode is unchanged.</summary>
    public void SetSplit(bool split)
    {
        if (_split == split) return;
        _split = split;
        Rebuild();
    }

    private void Rebuild()
    {
        var root = new StackPanel { Orientation = Orientation.Vertical };

        if (_loading)
            root.Children.Add(Message("Loading…"));
        else if (_diff is not { Files.Count: > 0 })
            root.Children.Add(Message(_note ?? "No changes."));
        else
            foreach (var file in _diff.Value.Files)
                root.Children.Add(FileSection(file));

        Child = root;
    }

    private Control FileSection(GitDiffFile file)
    {
        var panel = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 0, 0, 10) };

        panel.Children.Add(new Border
        {
            Background = FileBarBg,
            Padding = new Thickness(10, 6),
            Child = Selectable(FileLabel(file), PathSize, TitleBrush, FontWeight.SemiBold),
        });

        if (file.IsBinary)
            panel.Children.Add(Message("Binary file — not shown."));
        else if (file.Hunks.Count == 0)
            panel.Children.Add(Message("No textual changes (mode/rename only)."));
        else
        {
            int budget = MaxLinesPerFile;
            foreach (var hunk in file.Hunks)
            {
                panel.Children.Add(HunkHeader(hunk.Header));
                panel.Children.Add(_split ? SplitBody(hunk, ref budget) : UnifiedBody(hunk, ref budget));
                if (budget <= 0)
                {
                    panel.Children.Add(Message("… diff truncated (file too large)."));
                    break;
                }
            }
        }

        return panel;
    }

    // Unified: one selectable block, each line prefixed by its +/- marker and coloured by kind.
    private Control UnifiedBody(GitDiffHunk hunk, ref int budget)
    {
        var inlines = new InlineCollection();
        foreach (var line in hunk.Lines)
        {
            if (budget-- <= 0) break;
            var (brush, marker) = line.Kind switch
            {
                GitDiffLineKind.Added => (AddedBrush, "+ "),
                GitDiffLineKind.Removed => (RemovedBrush, "- "),
                GitDiffLineKind.Meta => (MutedBrush, ""),
                _ => (ContextBrush, "  "),
            };
            AppendLine(inlines, marker + line.Text, brush);
        }
        return Body(inlines);
    }

    // Split: removed lines (left) pair with the following added lines (right); context sits on both sides;
    // blanks pad the shorter side so the two columns stay row-aligned.
    private Control SplitBody(GitDiffHunk hunk, ref int budget)
    {
        var left = new InlineCollection();
        var right = new InlineCollection();
        var pendingRemoved = new List<string>();
        var pendingAdded = new List<string>();

        void Flush()
        {
            int rows = Math.Max(pendingRemoved.Count, pendingAdded.Count);
            for (int i = 0; i < rows; i++)
            {
                AppendLine(left, i < pendingRemoved.Count ? pendingRemoved[i] : "", RemovedBrush);
                AppendLine(right, i < pendingAdded.Count ? pendingAdded[i] : "", AddedBrush);
            }
            pendingRemoved.Clear();
            pendingAdded.Clear();
        }

        foreach (var line in hunk.Lines)
        {
            if (budget-- <= 0) break;
            switch (line.Kind)
            {
                case GitDiffLineKind.Removed: pendingRemoved.Add(line.Text); break;
                case GitDiffLineKind.Added: pendingAdded.Add(line.Text); break;
                default: // context / meta — flush the pending change block, then echo on both sides
                    Flush();
                    var brush = line.Kind == GitDiffLineKind.Meta ? MutedBrush : ContextBrush;
                    AppendLine(left, line.Text, brush);
                    AppendLine(right, line.Text, brush);
                    break;
            }
        }
        Flush();

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
        var l = Body(left); l.SetValue(Grid.ColumnProperty, 0);
        var r = Body(right); r.SetValue(Grid.ColumnProperty, 1); r.Margin = new Thickness(10, 0, 0, 0);
        grid.Children.Add(l);
        grid.Children.Add(r);
        return grid;
    }

    // A selectable monospace block from a prepared inline collection (one Run + LineBreak per line).
    private static SelectableTextBlock Body(InlineCollection inlines) => new()
    {
        Inlines = inlines,
        FontFamily = Mono,
        FontSize = LineSize,
        TextWrapping = TextWrapping.NoWrap,
        SelectionBrush = SelectionBrush,
        Padding = new Thickness(10, 2),
    };

    private static void AppendLine(InlineCollection inlines, string text, IBrush brush)
    {
        if (text.Length > 0)
            inlines.Add(new Run(text) { Foreground = brush });
        inlines.Add(new LineBreak());
    }

    private static Control HunkHeader(string header) => new SelectableTextBlock
    {
        Text = header,
        FontFamily = Mono,
        FontSize = HunkSize,
        Foreground = HunkBrush,
        SelectionBrush = SelectionBrush,
        Padding = new Thickness(10, 4, 10, 2),
    };

    private static SelectableTextBlock Selectable(string text, double size, IBrush brush, FontWeight weight) => new()
    {
        Text = text,
        FontSize = size,
        FontWeight = weight,
        Foreground = brush,
        SelectionBrush = SelectionBrush,
    };

    private static Control Message(string text) => new TextBlock
    {
        Text = text,
        Foreground = MutedBrush,
        FontSize = PathSize,
        Margin = new Thickness(14, 10),
    };

    private static string FileLabel(GitDiffFile file) => (file.OldPath, file.NewPath) switch
    {
        (null, { } n) => $"added: {n}",
        ({ } o, null) => $"deleted: {o}",
        ({ } o, { } n) when o != n => $"{o} → {n}",
        ({ } o, _) => o,
        _ => "(unknown)",
    };
}

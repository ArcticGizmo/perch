using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Perch.Avalonia.Theming;
using Perch.Data;

namespace Perch.Avalonia.Views;

/// <summary>One labelled diff to render — e.g. ("Staged", …) / ("Unstaged", …) when a file has both, or a
/// single unlabelled section for a commit or a plain file diff.</summary>
internal readonly record struct DiffSection(string? Label, GitDiff Diff);

/// <summary>
/// The unified-or-split diff surface for the Change Review window. Each hunk is laid out as a grid with one
/// row per source line: a line-number gutter column and a selectable text column (a
/// <see cref="SelectableTextBlock"/>, so text can be selected and copied). One row per logical line means
/// the gutter stays aligned and lines wrap cleanly (the row grows, the number top-aligns) without depending
/// on any text-layout introspection. Added lines are tinted <see cref="Palette.Green"/>, removed
/// <see cref="Palette.Red"/>, context muted, with a faint full-width row band.
///
/// Layouts toggle via <see cref="SetSplit"/> (unified vs side-by-side) and wrapping via
/// <see cref="SetWrap"/>. Rebuilds its visual tree on any change; hosted in a <c>ScrollViewer</c>.
/// </summary>
internal sealed class DiffView : Border
{
    private static readonly Color BodyBg = Color.FromRgb(18, 18, 24);
    private static readonly IBrush FileBarBg = new SolidColorBrush(Color.FromRgb(30, 30, 42));
    private static readonly IBrush SectionBarBg = new SolidColorBrush(Color.FromRgb(40, 40, 56));
    private static readonly IBrush TitleBrush = new SolidColorBrush(Palette.Title);
    private static readonly IBrush MutedBrush = new SolidColorBrush(Palette.Muted);
    private static readonly IBrush GutterBrush = new SolidColorBrush(Color.FromRgb(110, 110, 132));
    private static readonly IBrush ContextBrush = new SolidColorBrush(Color.FromRgb(190, 190, 205));
    private static readonly IBrush AddedBrush = new SolidColorBrush(Palette.Green);
    private static readonly IBrush RemovedBrush = new SolidColorBrush(Palette.Red);
    private static readonly IBrush HunkBrush = new SolidColorBrush(Palette.Accent);
    private static readonly IBrush SelectionBrush = new SolidColorBrush(Color.FromArgb(90, 96, 165, 250));
    private static readonly IBrush AddedBandBg = new SolidColorBrush(Color.FromArgb(36, 34, 197, 94));
    private static readonly IBrush RemovedBandBg = new SolidColorBrush(Color.FromArgb(36, 239, 68, 68));

    private static readonly FontFamily Mono = new("Cascadia Code, Consolas, Menlo, monospace");
    private const double LineSize = 12.5, PathSize = 13, HunkSize = 12;

    // Above this many lines a single file's diff is truncated with a note — a safety valve so a pathological
    // (generated / minified) file can't spawn tens of thousands of row controls.
    private const int MaxLinesPerFile = 4000;

    private IReadOnlyList<DiffSection> _sections = [];
    private string? _note = "Select a file or commit to see its diff.";
    private bool _loading;
    private bool _split;
    private bool _wrap = true;

    public DiffView()
    {
        Background = new SolidColorBrush(BodyBg);
        Padding = new Thickness(0, 0, 0, 12);
        Rebuild();
    }

    public void SetLoading()
    {
        _loading = true;
        _sections = [];
        Rebuild();
    }

    /// <summary>Show a single diff (commit or plain file), or clear to a placeholder <paramref name="note"/>.</summary>
    public void SetDiff(GitDiff? diff, string? note)
    {
        _loading = false;
        _sections = diff is { Files.Count: > 0 } d ? [new DiffSection(null, d)] : [];
        _note = note;
        Rebuild();
    }

    /// <summary>Show one or more labelled diff sections (e.g. Staged + Unstaged for one file).</summary>
    public void SetSections(IReadOnlyList<DiffSection> sections, string? note)
    {
        _loading = false;
        _sections = sections;
        _note = note;
        Rebuild();
    }

    /// <summary>Switch between unified (false) and side-by-side split (true) layout.</summary>
    public void SetSplit(bool split)
    {
        if (_split == split) return;
        _split = split;
        Rebuild();
    }

    /// <summary>Wrap long lines (true) or let them run off the right edge (false).</summary>
    public void SetWrap(bool wrap)
    {
        if (_wrap == wrap) return;
        _wrap = wrap;
        Rebuild();
    }

    private void Rebuild()
    {
        var root = new StackPanel { Orientation = Orientation.Vertical };

        if (_loading)
            root.Children.Add(Message("Loading…"));
        else if (_sections.Count == 0)
            root.Children.Add(Message(_note ?? "No changes."));
        else
            foreach (var section in _sections)
            {
                if (section.Label is { } label)
                    root.Children.Add(SectionHeader(label));
                foreach (var file in section.Diff.Files)
                    root.Children.Add(FileSection(file));
            }

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
                var (oldStart, newStart) = HunkStarts(hunk.Header);
                panel.Children.Add(HunkHeader(hunk.Header));
                panel.Children.Add(_split
                    ? SplitHunk(hunk, oldStart, newStart, ref budget)
                    : UnifiedHunk(hunk, oldStart, newStart, ref budget));
                if (budget <= 0)
                {
                    panel.Children.Add(Message("… diff truncated (file too large)."));
                    break;
                }
            }
        }

        return panel;
    }

    // One row per source line: [number gutter | text]. The number is new# (old# for removed lines); a faint
    // band tints added/removed rows. A single grid per hunk shares the gutter column width, so numbers line
    // up; each text cell is its own SelectableTextBlock (selection is per line).
    private Control UnifiedHunk(GitDiffHunk hunk, int oldNo, int newNo, ref int budget)
    {
        var grid = NewGrid("Auto,*");
        int row = 0;
        foreach (var line in hunk.Lines)
        {
            if (budget-- <= 0) break;
            var (brush, band, marker, num) = line.Kind switch
            {
                GitDiffLineKind.Added => (AddedBrush, AddedBandBg, "+ ", (newNo++).ToString()),
                GitDiffLineKind.Removed => (RemovedBrush, RemovedBandBg, "- ", (oldNo++).ToString()),
                GitDiffLineKind.Meta => (MutedBrush, null, "", ""),
                _ => (ContextBrush, (IBrush?)null, "  ", NextContext(ref oldNo, ref newNo)),
            };
            AddRow(grid, row, band, 0, 2);
            AddNumber(grid, row, 0, num);
            AddText(grid, row, 1, marker + line.Text, brush);
            row++;
        }
        return grid;
    }

    // Side-by-side: removed lines (left, old#) pair with the following added lines (right, new#); context
    // echoes on both sides; blanks pad the shorter side so paired rows line up. Columns: leftNum, leftText,
    // rightNum, rightText.
    private Control SplitHunk(GitDiffHunk hunk, int oldNo, int newNo, ref int budget)
    {
        var grid = NewGrid("Auto,*,Auto,*");
        int row = 0;
        var pendingR = new List<(string text, string num)>();
        var pendingA = new List<(string text, string num)>();

        void Flush()
        {
            int n = Math.Max(pendingR.Count, pendingA.Count);
            for (int i = 0; i < n; i++)
            {
                if (i < pendingR.Count)
                {
                    AddRow(grid, row, RemovedBandBg, 0, 2);
                    AddNumber(grid, row, 0, pendingR[i].num);
                    AddText(grid, row, 1, pendingR[i].text, RemovedBrush);
                }
                if (i < pendingA.Count)
                {
                    AddRow(grid, row, AddedBandBg, 2, 2);
                    AddNumber(grid, row, 2, pendingA[i].num);
                    AddText(grid, row, 3, pendingA[i].text, AddedBrush);
                }
                row++;
            }
            pendingR.Clear();
            pendingA.Clear();
        }

        foreach (var line in hunk.Lines)
        {
            if (budget-- <= 0) break;
            switch (line.Kind)
            {
                case GitDiffLineKind.Removed: pendingR.Add((line.Text, (oldNo++).ToString())); break;
                case GitDiffLineKind.Added: pendingA.Add((line.Text, (newNo++).ToString())); break;
                default: // context / meta — flush pending change block, then echo on both sides
                    Flush();
                    var brush = line.Kind == GitDiffLineKind.Meta ? MutedBrush : ContextBrush;
                    string ln = line.Kind == GitDiffLineKind.Meta ? "" : (newNo).ToString();
                    string lo = line.Kind == GitDiffLineKind.Meta ? "" : (oldNo).ToString();
                    AddNumber(grid, row, 0, lo);
                    AddText(grid, row, 1, line.Text, brush);
                    AddNumber(grid, row, 2, ln);
                    AddText(grid, row, 3, line.Text, brush);
                    if (line.Kind != GitDiffLineKind.Meta) { oldNo++; newNo++; }
                    row++;
                    break;
            }
        }
        Flush();
        return grid;
    }

    // ---- row/grid helpers ----

    private static Grid NewGrid(string cols) => new()
    {
        ColumnDefinitions = new ColumnDefinitions(cols),
    };

    // Ensures the grid has a row at index and returns after growing RowDefinitions as needed.
    private static void EnsureRow(Grid grid, int row)
    {
        while (grid.RowDefinitions.Count <= row)
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
    }

    // A faint full-width band behind a row, spanning from column start for span columns. Added first so it
    // sits behind the number/text.
    private static void AddRow(Grid grid, int row, IBrush? band, int col, int span)
    {
        EnsureRow(grid, row);
        if (band is null) return;
        var b = new Border { Background = band };
        b.SetValue(Grid.RowProperty, row);
        b.SetValue(Grid.ColumnProperty, col);
        b.SetValue(Grid.ColumnSpanProperty, span);
        grid.Children.Add(b);
    }

    private void AddNumber(Grid grid, int row, int col, string num)
    {
        EnsureRow(grid, row);
        var tb = new TextBlock
        {
            Text = num,
            FontFamily = Mono,
            FontSize = LineSize,
            Foreground = GutterBrush,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Padding = new Thickness(8, 1, 8, 1),
            MinWidth = 34,
        };
        tb.SetValue(Grid.RowProperty, row);
        tb.SetValue(Grid.ColumnProperty, col);
        grid.Children.Add(tb);
    }

    private void AddText(Grid grid, int row, int col, string text, IBrush brush)
    {
        EnsureRow(grid, row);
        var tb = new SelectableTextBlock
        {
            Text = text,
            FontFamily = Mono,
            FontSize = LineSize,
            Foreground = brush,
            TextWrapping = _wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            SelectionBrush = SelectionBrush,
            Padding = new Thickness(2, 1, 8, 1),
        };
        tb.SetValue(Grid.RowProperty, row);
        tb.SetValue(Grid.ColumnProperty, col);
        grid.Children.Add(tb);
    }

    private static string NextContext(ref int oldNo, ref int newNo)
    {
        string n = newNo.ToString();
        oldNo++;
        newNo++;
        return n;
    }

    private static Control SectionHeader(string label) => new Border
    {
        Background = SectionBarBg,
        Padding = new Thickness(10, 5),
        Child = new TextBlock { Text = label, Foreground = TitleBrush, FontSize = 12, FontWeight = FontWeight.Bold },
    };

    private static Control HunkHeader(string header) => new SelectableTextBlock
    {
        Text = header,
        FontFamily = Mono,
        FontSize = HunkSize,
        Foreground = HunkBrush,
        SelectionBrush = SelectionBrush,
        Padding = new Thickness(8, 6, 8, 2),
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

    // Parses the start line numbers from a hunk header "@@ -<oldStart>[,n] +<newStart>[,n] @@ …".
    private static (int OldStart, int NewStart) HunkStarts(string header)
    {
        int oldStart = 1, newStart = 1;
        foreach (var tok in header.Split(' '))
        {
            if (tok.Length < 2) continue;
            if (tok[0] == '-') oldStart = LeadingInt(tok.AsSpan(1), oldStart);
            else if (tok[0] == '+') newStart = LeadingInt(tok.AsSpan(1), newStart);
        }
        return (oldStart, newStart);

        static int LeadingInt(ReadOnlySpan<char> s, int fallback)
        {
            int comma = s.IndexOf(',');
            var num = comma >= 0 ? s[..comma] : s;
            return int.TryParse(num, out var v) ? v : fallback;
        }
    }
}

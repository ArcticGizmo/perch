using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
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

    // Collapsed file sections, keyed per file so the state survives a rebuild (wrap/split toggle). The key
    // being built for the file currently under construction, so AddText can tag each line with it.
    private readonly HashSet<string> _collapsed = new();
    private string _currentFileKey = "";

    // Find state. _lines is every body line control in render order (for split: row0-left, row0-right,
    // row1-left, … — i.e. by line number, not whole-left-column-then-right), each tagged with its file key
    // so collapsed files are skipped. _matches indexes into those in the same order.
    private readonly List<(SelectableTextBlock Tb, string Key)> _lines = new();
    private readonly List<(SelectableTextBlock Tb, int Start, int Len)> _matches = new();
    private string _query = "";
    private int _matchIdx = -1;
    private SelectableTextBlock? _highlighted;

    // Line-range selection (GitHub-style): click a gutter line number to select that line, shift-click or
    // drag to extend a contiguous range, Ctrl+C copies the lines' content. Lines are grouped into streams
    // (unified: one; split: 0 = left/old, 1 = right/new) so a range stays within one side. Anchor/focus are
    // positions within the active stream.
    private readonly Dictionary<int, List<LineEntry>> _streams = new();
    private int _selStream = -1, _selAnchor = -1, _selFocus = -1;
    private bool _dragging;
    private static readonly IBrush LineSelBg = new SolidColorBrush(Color.FromArgb(70, 96, 165, 250));

    /// <summary>Raised after a search/navigation changes the match set — (current 1-based index or 0, total).</summary>
    public event Action<int, int>? SearchResultsChanged;

    public DiffView()
    {
        Background = new SolidColorBrush(BodyBg);
        Padding = new Thickness(0, 0, 0, 12);
        // A pointer release anywhere ends a line-range drag (handledEventsToo so a child handling the release
        // still ends the drag).
        AddHandler(PointerReleasedEvent, (_, _) => _dragging = false, handledEventsToo: true);
        // Tunnelled Ctrl+C: when a line-range selection is active, copy it before a focused text cell can
        // copy its own within-line selection. (When focus is outside the diff, the window's handler catches
        // it instead.)
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.C && e.KeyModifiers.HasFlag(KeyModifiers.Control) && TryCopyLineSelection())
                e.Handled = true;
        }, RoutingStrategies.Tunnel);
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

    // ---- find (Ctrl+F) ----

    /// <summary>Set the case-insensitive find query over the current diff, select+scroll to the first match,
    /// and report the result count. Matches are ordered by line number (for split: interleaved by row, not
    /// whole-left-column-then-right). An empty query clears the search.</summary>
    public void SetSearch(string query)
    {
        _query = query ?? "";
        _matchIdx = 0;
        RecomputeMatches();
        Highlight();
        RaiseResults();
    }

    /// <summary>Move to the next match (wraps around).</summary>
    public void NextMatch() => Move(1);

    /// <summary>Move to the previous match (wraps around).</summary>
    public void PrevMatch() => Move(-1);

    /// <summary>Clear the find query and any match highlight.</summary>
    public void ClearSearch()
    {
        _query = "";
        _matches.Clear();
        _matchIdx = -1;
        ClearHighlight();
        RaiseResults();
    }

    private void Move(int delta)
    {
        if (_matches.Count > 0)
        {
            _matchIdx = ((_matchIdx + delta) % _matches.Count + _matches.Count) % _matches.Count;
            Highlight();
        }
        RaiseResults();
    }

    // Rebuilds the match list by scanning the body lines (in render/line order), skipping lines in collapsed
    // files. Clamps the current index into range.
    private void RecomputeMatches()
    {
        _matches.Clear();
        if (_query.Length > 0)
            foreach (var (tb, keyOfLine) in _lines)
            {
                if (_collapsed.Contains(keyOfLine)) continue;
                var text = tb.Text ?? "";
                int i = 0;
                while ((i = text.IndexOf(_query, i, StringComparison.OrdinalIgnoreCase)) >= 0)
                {
                    _matches.Add((tb, i, _query.Length));
                    i += _query.Length;
                }
            }

        if (_matches.Count == 0) _matchIdx = -1;
        else if (_matchIdx < 0) _matchIdx = 0;
        else if (_matchIdx >= _matches.Count) _matchIdx = _matches.Count - 1;
    }

    // Selects the current match (using the built-in selection highlight) and scrolls it to the centre of
    // the viewport. Centring runs at Background priority so the layout is current (e.g. right after a
    // rebuild) when we measure the match's position.
    private void Highlight()
    {
        ClearHighlight();
        if (_matchIdx < 0 || _matchIdx >= _matches.Count) return;
        var (tb, start, len) = _matches[_matchIdx];
        tb.SelectionStart = start;
        tb.SelectionEnd = start + len;
        _highlighted = tb;
        Dispatcher.UIThread.Post(() => CenterOn(tb), DispatcherPriority.Background);
    }

    // Scrolls the enclosing ScrollViewer so that target is vertically centred (clamped to the extent).
    private void CenterOn(Control target)
    {
        if (this.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault() is not { } sv) return;
        if (target.TranslatePoint(new Point(0, 0), this) is not { } p) return;
        double targetCentre = p.Y + target.Bounds.Height / 2;
        double max = Math.Max(0, sv.Extent.Height - sv.Viewport.Height);
        double y = Math.Clamp(targetCentre - sv.Viewport.Height / 2, 0, max);
        sv.Offset = sv.Offset.WithY(y);
    }

    private void ClearHighlight()
    {
        if (_highlighted is { } prev)
        {
            prev.SelectionStart = 0;
            prev.SelectionEnd = 0;
        }
        _highlighted = null;
    }

    private void RaiseResults() =>
        SearchResultsChanged?.Invoke(_matches.Count == 0 ? 0 : _matchIdx + 1, _matches.Count);

    private void Rebuild()
    {
        _lines.Clear();
        _streams.Clear();
        _selStream = _selAnchor = _selFocus = -1;
        _dragging = false;
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
                    root.Children.Add(FileSection(section.Label, file));
            }

        Child = root;

        // The line controls are new after a rebuild, so recompute matches against them (keeping the query
        // and, where possible, the current position) and re-highlight.
        if (_query.Length > 0)
        {
            RecomputeMatches();
            Highlight();
            RaiseResults();
        }
    }

    private Control FileSection(string? sectionLabel, GitDiffFile file)
    {
        string key = (sectionLabel ?? "") + "\n" + FileLabel(file);
        bool collapsed = _collapsed.Contains(key);
        _currentFileKey = key;

        var chevron = new TextBlock
        {
            Text = collapsed ? "▸" : "▾",
            Foreground = MutedBrush, FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
        };
        var header = new Border
        {
            Background = FileBarBg,
            Padding = new Thickness(10, 6),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    chevron,
                    new TextBlock
                    {
                        Text = FileLabel(file), Foreground = TitleBrush, FontSize = PathSize,
                        FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                },
            },
        };

        var content = new StackPanel { Orientation = Orientation.Vertical, IsVisible = !collapsed };
        if (file.IsBinary)
            content.Children.Add(Message("Binary file — not shown."));
        else if (file.Hunks.Count == 0)
            content.Children.Add(Message("No textual changes (mode/rename only)."));
        else
        {
            int budget = MaxLinesPerFile;
            foreach (var hunk in file.Hunks)
            {
                var (oldStart, newStart) = HunkStarts(hunk.Header);
                content.Children.Add(HunkHeader(hunk.Header));
                content.Children.Add(_split
                    ? SplitHunk(hunk, oldStart, newStart, ref budget)
                    : UnifiedHunk(hunk, oldStart, newStart, ref budget));
                if (budget <= 0)
                {
                    content.Children.Add(Message("… diff truncated (file too large)."));
                    break;
                }
            }
        }

        // Click the header bar to collapse/expand this file (persisted via _collapsed). Toggled in place so
        // the diff scroll position is preserved; matches are recomputed since collapsed lines aren't searched.
        header.PointerPressed += (_, _) =>
        {
            bool nowCollapsed = !_collapsed.Remove(key); // Remove returns false when it wasn't there → now collapse
            if (nowCollapsed) _collapsed.Add(key);
            content.IsVisible = !nowCollapsed;
            chevron.Text = nowCollapsed ? "▸" : "▾";
            if (_query.Length > 0) { RecomputeMatches(); Highlight(); RaiseResults(); }
        };

        return new StackPanel
        {
            Orientation = Orientation.Vertical, Margin = new Thickness(0, 0, 0, 10),
            Children = { header, content },
        };
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
            var number = AddNumber(grid, row, 0, num);
            var text = AddText(grid, row, 1, marker + line.Text, brush);
            if (num.Length > 0) RegisterLine(0, line.Text, number, text);
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
                    var num = AddNumber(grid, row, 0, pendingR[i].num);
                    var txt = AddText(grid, row, 1, pendingR[i].text, RemovedBrush);
                    RegisterLine(0, pendingR[i].text, num, txt);
                }
                if (i < pendingA.Count)
                {
                    AddRow(grid, row, AddedBandBg, 2, 2);
                    var num = AddNumber(grid, row, 2, pendingA[i].num);
                    var txt = AddText(grid, row, 3, pendingA[i].text, AddedBrush);
                    RegisterLine(1, pendingA[i].text, num, txt);
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
                    var lNum = AddNumber(grid, row, 0, lo);
                    var lTxt = AddText(grid, row, 1, line.Text, brush);
                    var rNum = AddNumber(grid, row, 2, ln);
                    var rTxt = AddText(grid, row, 3, line.Text, brush);
                    if (line.Kind != GitDiffLineKind.Meta)
                    {
                        RegisterLine(0, line.Text, lNum, lTxt);
                        RegisterLine(1, line.Text, rNum, rTxt);
                        oldNo++; newNo++;
                    }
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

    private TextBlock AddNumber(Grid grid, int row, int col, string num)
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
        return tb;
    }

    private SelectableTextBlock AddText(Grid grid, int row, int col, string text, IBrush brush)
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
        _lines.Add((tb, _currentFileKey)); // in render order → search order is by line number
        return tb;
    }

    // ---- line-range selection ----

    // Registers one selectable diff line (a gutter number + its text) in a stream, and wires the gutter to
    // start/extend a line-range selection. Clicking sets the anchor; Shift-click or drag extends it.
    private void RegisterLine(int stream, string content, TextBlock number, SelectableTextBlock text)
    {
        if (!_streams.TryGetValue(stream, out var list)) { list = new(); _streams[stream] = list; }
        var entry = new LineEntry(stream, list.Count, content, number, text);
        list.Add(entry);

        number.Cursor = new Cursor(StandardCursorType.Hand);
        number.PointerPressed += (_, e) =>
        {
            bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            if (shift && _selStream == stream && _selAnchor >= 0)
                _selFocus = entry.Pos;
            else
                { _selStream = stream; _selAnchor = _selFocus = entry.Pos; }
            _dragging = true;
            ApplyLineHighlight();
            e.Handled = true;
        };
        number.PointerEntered += (_, _) =>
        {
            if (_dragging && _selStream == stream) { _selFocus = entry.Pos; ApplyLineHighlight(); }
        };
    }

    // Tints the selected line range (number + text cells) in the active stream; clears the rest.
    private void ApplyLineHighlight()
    {
        foreach (var (stream, list) in _streams)
        {
            int lo = -1, hi = -1;
            if (stream == _selStream && _selAnchor >= 0)
            {
                lo = Math.Min(_selAnchor, _selFocus);
                hi = Math.Max(_selAnchor, _selFocus);
            }
            foreach (var e in list)
            {
                bool sel = e.Pos >= lo && e.Pos <= hi;
                e.Number.Background = sel ? LineSelBg : null;
                e.Text.Background = sel ? LineSelBg : null;
            }
        }
    }

    /// <summary>Copies the current line-range selection's content to the clipboard (one line per row, no
    /// markers or line numbers). Returns false when nothing is line-selected, so Ctrl+C can fall through to
    /// the normal within-line text copy.</summary>
    public bool TryCopyLineSelection()
    {
        if (_selStream < 0 || _selAnchor < 0 || !_streams.TryGetValue(_selStream, out var list)) return false;
        int lo = Math.Min(_selAnchor, _selFocus), hi = Math.Max(_selAnchor, _selFocus);
        if (lo < 0 || hi < lo) return false;
        var sb = new System.Text.StringBuilder();
        for (int i = lo; i <= hi && i < list.Count; i++)
        {
            if (i > lo) sb.Append('\n');
            sb.Append(list[i].Content);
        }
        TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(sb.ToString());
        return true;
    }

    private sealed record LineEntry(int Stream, int Pos, string Content, TextBlock Number, SelectableTextBlock Text);

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

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

/// <summary>Whether a diff section's hunks can be staged/unstaged, and which way — drives the per-hunk
/// button in the diff pane. <see cref="None"/> for read-only sections (a commit, or a clean file).</summary>
internal enum HunkStageAction { None, Stage, Unstage }

/// <summary>One labelled diff to render — e.g. ("Staged", …) / ("Unstaged", …) when a file has both, or a
/// single unlabelled section for a commit or a plain file diff. <see cref="Action"/> makes the section's
/// hunks stage- or unstage-able (the working-tree sections of the Tree window's WIP node).</summary>
internal readonly record struct DiffSection(string? Label, GitDiff Diff, HunkStageAction Action = HunkStageAction.None);

/// <summary>Raised when the user clicks a hunk's stage/unstage button — carries which way, the file path,
/// and the hunk's header (the range identifies it for <c>git apply</c>).</summary>
internal readonly record struct HunkStageRequest(HunkStageAction Action, string? Path, string HunkHeader);

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
    // Palette-driven brushes. Instance (not static) so the diff can render light or dark independently of the
    // rest of the app (the Tree window's per-window light toggle) — SetLight swaps the whole set and rebuilds.
    private Color BodyBg;
    private IBrush FileBarBg = null!;
    private IBrush SectionBarBg = null!;
    private IBrush TitleBrush = null!;
    private IBrush MutedBrush = null!;
    private IBrush GutterBrush = null!;
    private IBrush ContextBrush = null!;
    private IBrush AddedBrush = null!;
    private IBrush RemovedBrush = null!;
    private IBrush HunkBrush = null!;
    private IBrush SelectionBrush = null!;
    private IBrush AddedBandBg = null!;
    private IBrush RemovedBandBg = null!;
    private IBrush MatchBg = null!;         // all matches (yellow)
    private IBrush CurrentMatchBg = null!;  // current match (orange)
    private IBrush LineSelBg = null!;       // whole-line selection wash
    private bool _light;

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
    private readonly List<(TextBlock Tb, string Key)> _lines = new();
    private readonly List<(TextBlock Tb, int Start, int Len)> _matches = new();
    private string _query = "";
    private int _matchIdx = -1;
    private HighlightLayer? _highlightLayer; // owner-drawn layer behind the text that paints matches + selection

    // Two mutually-exclusive selection modes, distinguished by _selKind:
    //  - Line: GitHub-style whole-line range - click a gutter line number, shift-click or drag over numbers
    //    to extend a contiguous range. Painted via the row cells' Background.
    //  - Char: character-level selection that can span lines - press+drag over the text body. Painted by the
    //    HighlightLayer (same HitTestTextRange machinery as find), so it can cross line boundaries which a
    //    per-line SelectableTextBlock never could.
    // Both are keyed to a "stream" (unified: one; split: 0 = left/old, 1 = right/new) so a range stays within
    // one side. Anchor/focus are positions within the active stream.
    private enum SelKind { None, Line, Char }
    private SelKind _selKind = SelKind.None;
    private readonly Dictionary<int, List<LineEntry>> _streams = new();
    private int _selStream = -1, _selAnchor = -1, _selFocus = -1;               // line mode: line positions
    private int _charStream = -1;                                               // char mode: active stream...
    private int _charAnchorPos = -1, _charAnchorCh = -1;                        // ...anchor (line pos, char)...
    private int _charFocusPos = -1, _charFocusCh = -1;                          // ...and focus (line pos, char)
    private bool _dragging;
    private Point _dragPtViewport;                                             // last drag point, in ScrollViewer space
    private DispatcherTimer? _autoScroll;                                       // edge auto-scroll while char-dragging

    /// <summary>Raised after a search/navigation changes the match set — (current 1-based index or 0, total).</summary>
    public event Action<int, int>? SearchResultsChanged;

    /// <summary>Raised when the user clicks a hunk's stage/unstage button (only present on sections whose
    /// <see cref="DiffSection.Action"/> isn't <see cref="HunkStageAction.None"/>).</summary>
    public event Action<HunkStageRequest>? HunkStageRequested;

    public DiffView()
    {
        ApplyDiffPalette(); // sets every brush + Background for the current (default dark) mode
        Padding = new Thickness(0, 0, 0, 12);
        Cursor = new Cursor(StandardCursorType.Ibeam); // the body reads as selectable text; gutter overrides to Hand
        // Click anywhere in the body (line ends, marker gutter, gaps) to start a char selection. Bubbling, so
        // it only fires for presses a child (gutter number / text cell / header) didn't already handle.
        AddHandler(PointerPressedEvent, OnBodyPointerPressed, RoutingStrategies.Bubble);
        // A pointer release anywhere ends a drag and releases any capture we took for a char-selection drag
        // (handledEventsToo so a child handling the release still ends the drag).
        AddHandler(PointerReleasedEvent, (_, e) =>
        {
            _dragging = false;
            _autoScroll?.Stop();
            e.Pointer.Capture(null);
        }, handledEventsToo: true);
        // While a char-selection drag is active the pointer is captured to this control, so every move lands
        // here; we resolve the line+char under it globally (across line controls) to extend the selection.
        AddHandler(PointerMovedEvent, OnPointerMoved, handledEventsToo: true);
        // Tunnelled Ctrl+C: when a diff selection is active, copy it before a focused cell can copy its own
        // selection. (When focus is outside the diff, the window's handler catches it instead.)
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.C && e.KeyModifiers.HasFlag(KeyModifiers.Control) && TryCopySelection())
                e.Handled = true;
        }, RoutingStrategies.Tunnel);
        _autoScroll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _autoScroll.Tick += (_, _) => AutoScrollTick();
        Rebuild();
    }

    /// <summary>Switches the diff between light and dark rendering, independent of the app theme (the Tree
    /// window's per-window toggle). Swaps the whole brush set and rebuilds.</summary>
    public void SetLight(bool light)
    {
        if (_light == light) return;
        _light = light;
        ApplyDiffPalette();
        Rebuild();
    }

    // Fills every colour field for the current mode and sets the surface background. Dark mirrors the original
    // literals (and the Palette-derived add/remove/hunk hues); light uses darker, print-legible variants.
    private void ApplyDiffPalette()
    {
        IBrush B(byte r, byte g, byte b) => new SolidColorBrush(Color.FromRgb(r, g, b));
        IBrush A(byte a, byte r, byte g, byte b) => new SolidColorBrush(Color.FromArgb(a, r, g, b));
        if (_light)
        {
            BodyBg = Color.FromRgb(0xFB, 0xFC, 0xFE);
            FileBarBg = B(0xF1, 0xF2, 0xF5);
            SectionBarBg = B(0xE6, 0xE9, 0xF0);
            TitleBrush = B(0x0D, 0x0E, 0x16);
            MutedBrush = B(0x5C, 0x60, 0x72);
            GutterBrush = B(0x9A, 0xA0, 0xAE);
            ContextBrush = B(0x24, 0x29, 0x2F);
            AddedBrush = B(0x1F, 0x88, 0x3D);
            RemovedBrush = B(0xCF, 0x22, 0x2E);
            HunkBrush = B(0x2F, 0x68, 0xE0);
            SelectionBrush = A(70, 0x2F, 0x68, 0xE0);
            AddedBandBg = A(30, 0x1F, 0x88, 0x3D);
            RemovedBandBg = A(28, 0xCF, 0x22, 0x2E);
            MatchBg = A(96, 0xF5, 0xD0, 0x00);
            CurrentMatchBg = A(200, 0xF0, 0x8A, 0x00);
            LineSelBg = A(55, 0x2F, 0x68, 0xE0);
        }
        else
        {
            BodyBg = Color.FromRgb(18, 18, 24);
            FileBarBg = B(30, 30, 42);
            SectionBarBg = B(40, 40, 56);
            TitleBrush = new SolidColorBrush(Palette.Title);
            MutedBrush = new SolidColorBrush(Palette.Muted);
            GutterBrush = B(110, 110, 132);
            ContextBrush = B(190, 190, 205);
            AddedBrush = new SolidColorBrush(Palette.Green);
            RemovedBrush = new SolidColorBrush(Palette.Red);
            HunkBrush = new SolidColorBrush(Palette.Accent);
            SelectionBrush = A(90, 96, 165, 250);
            AddedBandBg = A(36, 34, 197, 94);
            RemovedBandBg = A(36, 239, 68, 68);
            MatchBg = A(85, 250, 204, 21);
            CurrentMatchBg = A(190, 255, 158, 40);
            LineSelBg = A(70, 96, 165, 250);
        }
        Background = new SolidColorBrush(BodyBg);
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
        _highlightLayer?.InvalidateVisual();
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

    // Repaints all match highlights and scrolls the current match to the centre of the viewport. Centring
    // runs at Background priority so the layout is current (e.g. right after a rebuild) when measured.
    private void Highlight()
    {
        _highlightLayer?.InvalidateVisual();
        if (_matchIdx >= 0 && _matchIdx < _matches.Count)
            Dispatcher.UIThread.Post(() => CenterOn(_matches[_matchIdx].Tb), DispatcherPriority.Background);
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

    // Paints the char selection (behind) plus every match (subtle) and the current match (prominent) behind
    // the diff text, locating each rectangle from its line's text layout. Called by the layer at render time.
    private void RenderHighlights(DrawingContext ctx, Control layer)
    {
        RenderCharSelection(ctx, layer);
        for (int i = 0; i < _matches.Count; i++)
        {
            var (tb, start, len) = _matches[i];
            if (!tb.IsEffectivelyVisible || tb.TextLayout is not { } layout) continue;
            if (tb.TranslatePoint(new Point(tb.Padding.Left, tb.Padding.Top), layer) is not { } origin) continue;
            var brush = i == _matchIdx ? CurrentMatchBg : MatchBg;
            foreach (var r in layout.HitTestTextRange(start, len))
                ctx.FillRectangle(brush, new Rect(origin.X + r.X, origin.Y + r.Y, r.Width, r.Height));
        }
    }

    // Paints the character selection across every line in its range: the first line from the start char, the
    // last line up to the end char, and any lines between in full - each via the same HitTestTextRange the
    // matches use, so wrapped lines yield one rect per visual row and the selection tracks the wrap.
    private void RenderCharSelection(DrawingContext ctx, Control layer)
    {
        var (sp, sc, ep, ec) = NormalizedCharRange();
        if (sp < 0 || !_streams.TryGetValue(_charStream, out var list)) return;
        foreach (var e in list)
        {
            if (e.Pos < sp || e.Pos > ep) continue;
            var tb = e.Text;
            if (!tb.IsEffectivelyVisible || tb.TextLayout is not { } layout) continue;
            int len = tb.Text?.Length ?? 0;
            int start = e.Pos == sp ? Math.Clamp(sc, 0, len) : 0;
            int end = e.Pos == ep ? Math.Clamp(ec, 0, len) : len;
            if (end <= start) continue;
            if (tb.TranslatePoint(new Point(tb.Padding.Left, tb.Padding.Top), layer) is not { } origin) continue;
            foreach (var r in layout.HitTestTextRange(start, end - start))
                ctx.FillRectangle(LineSelBg, new Rect(origin.X + r.X, origin.Y + r.Y, r.Width, r.Height));
        }
    }

    private void RaiseResults() =>
        SearchResultsChanged?.Invoke(_matches.Count == 0 ? 0 : _matchIdx + 1, _matches.Count);

    private void Rebuild()
    {
        _lines.Clear();
        _streams.Clear();
        _selKind = SelKind.None;
        _selStream = _selAnchor = _selFocus = -1;
        _charStream = _charAnchorPos = _charAnchorCh = _charFocusPos = _charFocusCh = -1;
        _dragging = false;
        _autoScroll?.Stop();
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
                    root.Children.Add(FileSection(section.Label, file, section.Action));
            }

        // The match-highlight layer sits behind the content (first child) so highlights paint behind the
        // text; it reads the live match list at paint time.
        _highlightLayer = new HighlightLayer(this);
        Child = new Grid { Children = { _highlightLayer, root } };

        // The line controls are new after a rebuild, so recompute matches against them (keeping the query
        // and, where possible, the current position) and re-highlight.
        if (_query.Length > 0)
        {
            RecomputeMatches();
            Highlight();
            RaiseResults();
        }
    }

    private Control FileSection(string? sectionLabel, GitDiffFile file, HunkStageAction action)
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
            string? path = file.NewPath ?? file.OldPath;
            foreach (var hunk in file.Hunks)
            {
                var (oldStart, newStart) = HunkStarts(hunk.Header);
                content.Children.Add(HunkHeader(hunk.Header, action, path));
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
        header.PointerPressed += (_, e) =>
        {
            bool nowCollapsed = !_collapsed.Remove(key); // Remove returns false when it wasn't there → now collapse
            if (nowCollapsed) _collapsed.Add(key);
            content.IsVisible = !nowCollapsed;
            chevron.Text = nowCollapsed ? "▸" : "▾";
            if (_query.Length > 0) { RecomputeMatches(); Highlight(); RaiseResults(); }
            e.Handled = true; // consume, so the body's click-to-select doesn't fire on the header
        };

        return new StackPanel
        {
            Orientation = Orientation.Vertical, Margin = new Thickness(0, 0, 0, 10),
            Children = { header, content },
        };
    }

    // One row per source line: [number gutter | +/- marker | text]. The number is new# (old# for removed
    // lines); a faint band tints added/removed rows. The marker sits in its own non-selectable column so the
    // text cell holds pure code - char selection and copy then map straight to the line's content, with no
    // marker to strip. A single grid per hunk shares the gutter/marker column widths, so everything lines up.
    private Control UnifiedHunk(GitDiffHunk hunk, int oldNo, int newNo, ref int budget)
    {
        var grid = NewGrid("Auto,Auto,*");
        int row = 0;
        foreach (var line in hunk.Lines)
        {
            if (budget-- <= 0) break;
            var (brush, band, marker, num) = line.Kind switch
            {
                GitDiffLineKind.Added => (AddedBrush, AddedBandBg, "+", (newNo++).ToString()),
                GitDiffLineKind.Removed => (RemovedBrush, RemovedBandBg, "-", (oldNo++).ToString()),
                GitDiffLineKind.Meta => (MutedBrush, null, "", ""),
                _ => (ContextBrush, (IBrush?)null, "", NextContext(ref oldNo, ref newNo)),
            };
            AddRow(grid, row, band, 0, 3);
            var number = AddNumber(grid, row, 0, num);
            AddMarker(grid, row, 1, marker, brush);
            var text = AddText(grid, row, 2, line.Text, brush);
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

    // The +/- marker cell - its own non-selectable column so the adjacent text cell stays pure code.
    private TextBlock AddMarker(Grid grid, int row, int col, string marker, IBrush brush)
    {
        EnsureRow(grid, row);
        var tb = new TextBlock
        {
            Text = marker,
            FontFamily = Mono,
            FontSize = LineSize,
            Foreground = brush,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Padding = new Thickness(2, 1, 2, 1),
            MinWidth = 14,
        };
        tb.SetValue(Grid.RowProperty, row);
        tb.SetValue(Grid.ColumnProperty, col);
        grid.Children.Add(tb);
        return tb;
    }

    // The code cell. A plain TextBlock (not SelectableTextBlock): selection is done ourselves through the
    // HighlightLayer so it can span lines, which native per-control selection can't.
    private TextBlock AddText(Grid grid, int row, int col, string text, IBrush brush)
    {
        EnsureRow(grid, row);
        var tb = new TextBlock
        {
            Text = text,
            FontFamily = Mono,
            FontSize = LineSize,
            Foreground = brush,
            TextWrapping = _wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            Padding = new Thickness(2, 1, 8, 1),
        };
        tb.SetValue(Grid.RowProperty, row);
        tb.SetValue(Grid.ColumnProperty, col);
        grid.Children.Add(tb);
        _lines.Add((tb, _currentFileKey)); // in render order → search order is by line number
        return tb;
    }

    // ---- selection ----

    // Registers one selectable diff line (a gutter number + its text) in a stream, and wires both selection
    // modes: the gutter number starts/extends a whole-line range (Line mode); the text cell starts a
    // character-level drag (Char mode) that can span lines.
    private void RegisterLine(int stream, string content, TextBlock number, TextBlock text)
    {
        if (!_streams.TryGetValue(stream, out var list)) { list = new(); _streams[stream] = list; }
        var entry = new LineEntry(stream, list.Count, content, number, text);
        list.Add(entry);

        // Line mode - gutter number.
        number.Cursor = new Cursor(StandardCursorType.Hand);
        number.PointerPressed += (_, e) =>
        {
            bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            if (shift && _selKind == SelKind.Line && _selStream == stream && _selAnchor >= 0)
                _selFocus = entry.Pos;
            else
                { _selStream = stream; _selAnchor = _selFocus = entry.Pos; }
            _selKind = SelKind.Line;
            ClearCharSelection();
            _dragging = true;
            ApplyLineHighlight();
            e.Handled = true;
        };
        number.PointerEntered += (_, _) =>
        {
            if (_dragging && _selKind == SelKind.Line && _selStream == stream)
                { _selFocus = entry.Pos; ApplyLineHighlight(); }
        };

        // Char mode - text body. Press anchors, then the pointer is captured to the DiffView so the drag can
        // be resolved across line controls in OnPointerMoved.
        text.Cursor = new Cursor(StandardCursorType.Ibeam);
        text.PointerPressed += (_, e) =>
        {
            int ch = CharIndexAt(text, e.GetPosition(text));
            ClearLineSelection();
            _selKind = SelKind.Char;
            _charStream = stream;
            _charAnchorPos = _charFocusPos = entry.Pos;
            _charAnchorCh = _charFocusCh = ch;
            _dragging = true;
            _autoScroll?.Start();
            e.Pointer.Capture(this);
            _highlightLayer?.InvalidateVisual();
            e.Handled = true;
        };
    }

    // ---- line mode ----

    // Tints the selected line range (number + text cells) in the active stream; clears the rest.
    private void ApplyLineHighlight()
    {
        foreach (var (stream, list) in _streams)
        {
            int lo = -1, hi = -1;
            if (_selKind == SelKind.Line && stream == _selStream && _selAnchor >= 0)
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

    private void ClearLineSelection()
    {
        _selStream = _selAnchor = _selFocus = -1;
        ApplyLineHighlight();
    }

    // ---- char mode ----

    // The caret index within a text cell for a point in the cell's own coordinate space. The TextLayout is
    // laid out without the control's padding, so subtract it (mirrors the origin offset used when painting).
    private static int CharIndexAt(TextBlock tb, Point p)
    {
        if (tb.TextLayout is not { } layout) return 0;
        var hit = layout.HitTestPoint(new Point(p.X - tb.Padding.Left, p.Y - tb.Padding.Top));
        int idx = hit.TextPosition + (hit.IsTrailing ? 1 : 0);
        return Math.Clamp(idx, 0, tb.Text?.Length ?? 0);
    }

    // The line in <paramref name="stream"/> nearest a point in this control's (the scrolled content's)
    // coordinate space — the line whose vertical band contains the point, else the last line above it (so a
    // click in the empty space at the end of a line, in the marker gutter, or between rows still resolves to
    // a real caret) — plus the caret within it. Collapsed (hidden) lines are skipped. Null when the stream is
    // empty. This is the shared resolver for both starting (click anywhere) and extending (drag) a selection.
    private (LineEntry Line, int Ch)? NearestInStream(int stream, Point pView)
    {
        if (!_streams.TryGetValue(stream, out var list) || list.Count == 0) return null;

        LineEntry? chosen = null;
        foreach (var ent in list)
        {
            if (!ent.Text.IsEffectivelyVisible) continue;
            if (ent.Text.TranslatePoint(new Point(0, 0), this) is not { } top) continue;
            double bottom = top.Y + ent.Text.Bounds.Height;
            if (pView.Y >= top.Y && pView.Y <= bottom) { chosen = ent; break; } // inside this line's band
            if (top.Y <= pView.Y) chosen = ent;                                  // last line above the point
        }
        chosen ??= list.FirstOrDefault(l => l.Text.IsEffectivelyVisible) ?? list[0];

        int ch = this.TranslatePoint(pView, chosen.Text) is { } pInText ? CharIndexAt(chosen.Text, pInText) : 0;
        return (chosen, ch);
    }

    // Which stream's text column a horizontal position falls in (unified: always the one stream; split: left
    // vs right by nearest column). Lets a click choose the side before a line is resolved.
    private int StreamAtX(double x)
    {
        int best = -1;
        double bestDx = double.PositiveInfinity;
        foreach (var (stream, list) in _streams)
        {
            LineEntry? rep = list.FirstOrDefault(e => e.Text.IsEffectivelyVisible) ?? (list.Count > 0 ? list[0] : null);
            if (rep is null || rep.Text.TranslatePoint(new Point(0, 0), this) is not { } tl) continue;
            double left = tl.X, right = tl.X + rep.Text.Bounds.Width;
            double dx = x < left ? left - x : x > right ? x - right : 0;
            if (dx < bestDx) { bestDx = dx; best = stream; }
        }
        return best;
    }

    // Extends the char selection to a point (drag), via the shared line/caret resolver.
    private void ExtendCharTo(Point pView)
    {
        if (NearestInStream(_charStream, pView) is not { } hit) return;
        if (_charFocusPos != hit.Line.Pos || _charFocusCh != hit.Ch)
        {
            _charFocusPos = hit.Line.Pos;
            _charFocusCh = hit.Ch;
            _highlightLayer?.InvalidateVisual();
        }
    }

    // Click anywhere in the diff body (empty space at line ends, the marker gutter, between rows) starts a
    // character selection at the nearest caret — the GitKraken-style "click empty space to select" behaviour.
    // Only reached for presses no child handled (a gutter number starts line mode; the text cell and headers
    // consume their own), so those keep precedence.
    private void OnBodyPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Handled || _streams.Count == 0) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        var pView = e.GetPosition(this);
        int stream = StreamAtX(pView.X);
        if (stream < 0 || NearestInStream(stream, pView) is not { } hit) return;

        ClearLineSelection();
        _selKind = SelKind.Char;
        _charStream = stream;
        _charAnchorPos = _charFocusPos = hit.Line.Pos;
        _charAnchorCh = _charFocusCh = hit.Ch;
        _dragging = true;
        _autoScroll?.Start();
        e.Pointer.Capture(this);
        _highlightLayer?.InvalidateVisual();
        e.Handled = true;
    }

    // While char-dragging, scroll the viewport when the pointer nears its top/bottom edge, then re-resolve
    // the focus against the (fixed-on-screen) pointer so the selection keeps growing as content scrolls.
    private void AutoScrollTick()
    {
        if (!_dragging || _selKind != SelKind.Char) { _autoScroll?.Stop(); return; }
        if (ScrollViewer is not { } sv) return;
        const double edge = 24, step = 14;
        double max = Math.Max(0, sv.Extent.Height - sv.Viewport.Height);
        double y = _dragPtViewport.Y;
        if (y < edge && sv.Offset.Y > 0)
            sv.Offset = sv.Offset.WithY(Math.Max(0, sv.Offset.Y - step));
        else if (y > sv.Viewport.Height - edge && sv.Offset.Y < max)
            sv.Offset = sv.Offset.WithY(Math.Min(max, sv.Offset.Y + step));
        else
            return;
        if (sv.TranslatePoint(_dragPtViewport, this) is { } pv) ExtendCharTo(pv);
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragging || _selKind != SelKind.Char) return;
        if (ScrollViewer is { } sv) _dragPtViewport = e.GetPosition(sv);
        ExtendCharTo(e.GetPosition(this));
    }

    private void ClearCharSelection()
    {
        _charStream = _charAnchorPos = _charAnchorCh = _charFocusPos = _charFocusCh = -1;
        _highlightLayer?.InvalidateVisual();
    }

    // The char range in document order: (startPos, startChar, endPos, endChar). startPos < 0 when there is
    // no char selection.
    private (int SP, int SC, int EP, int EC) NormalizedCharRange()
    {
        if (_selKind != SelKind.Char || _charAnchorPos < 0 || _charFocusPos < 0) return (-1, -1, -1, -1);
        bool anchorFirst = _charAnchorPos < _charFocusPos ||
                           (_charAnchorPos == _charFocusPos && _charAnchorCh <= _charFocusCh);
        return anchorFirst
            ? (_charAnchorPos, _charAnchorCh, _charFocusPos, _charFocusCh)
            : (_charFocusPos, _charFocusCh, _charAnchorPos, _charAnchorCh);
    }

    private ScrollViewer? ScrollViewer => this.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault();

    // ---- copy ----

    /// <summary>Copies the active diff selection to the clipboard (char range, or whole-line range - one line
    /// per row, no markers or numbers). Returns false when nothing is selected, so Ctrl+C can fall through.</summary>
    public bool TryCopySelection() => _selKind switch
    {
        SelKind.Char => TryCopyCharSelection(),
        SelKind.Line => TryCopyLineSelection(),
        _ => false,
    };

    private bool TryCopyCharSelection()
    {
        if (!_streams.TryGetValue(_charStream, out var list)) return false;
        var (sp, sc, ep, ec) = NormalizedCharRange();
        if (sp < 0 || (sp == ep && sc == ec)) return false; // nothing, or a bare caret
        var sb = new System.Text.StringBuilder();
        bool first = true;
        foreach (var e in list)
        {
            if (e.Pos < sp || e.Pos > ep) continue;
            string c = e.Content ?? "";
            int start = e.Pos == sp ? Math.Clamp(sc, 0, c.Length) : 0;
            int end = e.Pos == ep ? Math.Clamp(ec, 0, c.Length) : c.Length;
            if (!first) sb.Append('\n');
            first = false;
            if (end > start) sb.Append(c, start, end - start);
        }
        TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(sb.ToString());
        return true;
    }

    private bool TryCopyLineSelection()
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

    private sealed record LineEntry(int Stream, int Pos, string Content, TextBlock Number, TextBlock Text);

    // A transparent, non-interactive layer behind the diff text that paints the find-match highlights. It
    // re-paints on layout changes (wrap reflow, collapse) and whenever the owner invalidates it (search /
    // navigation). Highlights are computed in the layer's own coordinate space, which it shares with the
    // scrolled content, so they stay aligned as the diff scrolls.
    private sealed class HighlightLayer : Control
    {
        private readonly DiffView _owner;

        public HighlightLayer(DiffView owner)
        {
            _owner = owner;
            IsHitTestVisible = false;
            LayoutUpdated += (_, _) => InvalidateVisual();
        }

        public override void Render(DrawingContext ctx) => _owner.RenderHighlights(ctx, this);
    }

    private static string NextContext(ref int oldNo, ref int newNo)
    {
        string n = newNo.ToString();
        oldNo++;
        newNo++;
        return n;
    }

    // These build coloured controls, so they read the instance brush set (not static — the brushes flip with
    // the light/dark toggle).
    private Control SectionHeader(string label) => new Border
    {
        Background = SectionBarBg,
        Padding = new Thickness(10, 5),
        Child = new TextBlock { Text = label, Foreground = TitleBrush, FontSize = 12, FontWeight = FontWeight.Bold },
    };

    private Control HunkHeader(string header, HunkStageAction action, string? path)
    {
        var text = new SelectableTextBlock
        {
            Text = header,
            FontFamily = Mono,
            FontSize = HunkSize,
            Foreground = HunkBrush,
            SelectionBrush = SelectionBrush,
            Padding = new Thickness(8, 6, 8, 2),
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (action == HunkStageAction.None)
            return text;

        // A small stage/unstage button on the right of the hunk header.
        var btn = new Button
        {
            Content = action == HunkStageAction.Stage ? "Stage hunk" : "Unstage hunk",
            FontSize = 11,
            Padding = new Thickness(8, 1),
            Margin = new Thickness(8, 2, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            [DockPanel.DockProperty] = Dock.Right,
        };
        btn.Click += (_, e) =>
        {
            HunkStageRequested?.Invoke(new HunkStageRequest(action, path, header));
            e.Handled = true;
        };
        return new DockPanel { LastChildFill = true, Children = { btn, text } };
    }

    private SelectableTextBlock Selectable(string text, double size, IBrush brush, FontWeight weight) => new()
    {
        Text = text,
        FontSize = size,
        FontWeight = weight,
        Foreground = brush,
        SelectionBrush = SelectionBrush,
    };

    private Control Message(string text) => new TextBlock
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

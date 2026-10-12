using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Perch.Avalonia.Theming;
using Perch.Data;

namespace Perch.Avalonia.Windows;

/// <summary>
/// Full-text search across every session transcript ("where did I discuss X?"). Type a phrase and every
/// transcript is streamed off the UI thread (<see cref="TranscriptSearch"/>); matching sessions stream in, grouped,
/// newest first (active ones on top, the same order as the history list), each with its first few hits as one-line
/// excerpts. Picking a hit raises <see cref="HitChosen"/> and the app opens the history viewer on it, with the find
/// bar lit on the query. By default only prompts and replies are searched; "Tools & thinking" widens it to tool
/// calls, tool output and thinking. A reused window, not a palette: it stays open while you read the hits.
/// </summary>
internal sealed class TranscriptSearchWindow : Window
{
    private const int HitsPerSession = 3;      // excerpts shown per session; the viewer's find bar steps the rest
    private const int MaxSessions = 300;       // groups shown at most (a very common word matches everything)
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan FlushEvery = TimeSpan.FromMilliseconds(120);

    private readonly SessionPalette _p = SessionPalette.Current;

    private readonly TextBox _search;
    private readonly Border _toolsToggle;
    private readonly TextBlock _toolsLabel;
    private readonly TextBlock _status;
    private readonly StackPanel _list;
    private readonly ScrollViewer _scroll;

    private bool _includeTools;
    private HashSet<string> _activeIds = new();

    // The running search: its cancellation, the query it's for, and the results it has delivered that aren't on
    // screen yet (filled from worker threads, drained on the UI thread by _flush).
    private CancellationTokenSource? _cts;
    private string _query = "";
    private readonly object _pendingLock = new();
    private List<(int Index, HistoryEntry Entry, TranscriptMatches Matches)> _pending = new();
    private int _scanned, _total;
    private bool _done;
    private System.Diagnostics.Stopwatch _clock = new();

    // What's on screen: the groups by list index (so a late result slots into its place) and the hit rows in
    // reading order, for ↑/↓.
    private readonly SortedList<int, Control> _groups = new();
    private readonly List<(Border Row, HistoryEntry Entry, TranscriptHit Hit)> _rows = new();
    private int _sessionsFound, _hitsFound;
    private int _selected = -1;

    private readonly DispatcherTimer _debounce;
    private readonly DispatcherTimer _flush;

    /// <summary>A hit was picked: (session, query, the hit). The app opens the history viewer on it.</summary>
    public event Action<HistoryEntry, string, TranscriptHit>? HitChosen;

    public TranscriptSearchWindow()
    {
        Title = "Search transcripts";
        Width = 760;
        Height = 640;
        MinWidth = 480;
        MinHeight = 320;
        Background = _p.Surface;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var magnifier = new global::Avalonia.Controls.Shapes.Path
        {
            Stroke = _p.Faint, StrokeThickness = 1.5, Width = 15, Height = 15,
            Data = Geometry.Parse("M2,6 a4,4 0 1 0 8,0 a4,4 0 1 0 -8,0 M9.2,9.2 L13,13"),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 12, 0),
            [DockPanel.DockProperty] = Dock.Left,
        };
        _search = new TextBox
        {
            FontFamily = _p.Body, FontSize = 16, Foreground = _p.Text, CaretBrush = _p.Brand,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            PlaceholderText = "Search what was said, across every session",
        };
        _search.TextChanged += (_, _) => { _debounce!.Stop(); _debounce.Start(); };

        _toolsLabel = new TextBlock
        {
            Text = "Tools & thinking", FontFamily = _p.Body, FontSize = 12.5, FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _toolsToggle = new Border
        {
            CornerRadius = SessionPalette.PillRadius, Padding = new Thickness(11, 5), BorderThickness = new Thickness(1),
            Cursor = new Cursor(StandardCursorType.Hand), VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0), Child = _toolsLabel, [DockPanel.DockProperty] = Dock.Right,
            [ToolTip.TipProperty] = "Also search tool calls, tool output and Claude's thinking",
        };
        _toolsToggle.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton != MouseButton.Left) return;
            _includeTools = !_includeTools;
            StyleToggle();
            StartSearch();
        };
        StyleToggle();

        var searchRow = new Border
        {
            Background = _p.Raised, BorderBrush = _p.Border, BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(16, 14), [DockPanel.DockProperty] = Dock.Top,
            Child = new DockPanel { LastChildFill = true, Children = { magnifier, _toolsToggle, _search } },
        };

        _status = new TextBlock
        {
            FontFamily = _p.Mono, FontSize = 11.5, Foreground = _p.Faint, Margin = new Thickness(18, 10, 18, 2),
            [DockPanel.DockProperty] = Dock.Top,
        };
        _list = new StackPanel { Margin = new Thickness(8, 4, 8, 12) };
        _scroll = new ScrollViewer
        {
            Content = _list,
            HorizontalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };

        var hint = new TextBlock
        {
            Text = "↑↓ move        ↵ open        esc close", FontFamily = _p.Mono, FontSize = 11,
            Foreground = _p.Faint, HorizontalAlignment = HorizontalAlignment.Center,
        };
        var footer = new Border
        {
            Background = _p.Raised, BorderBrush = _p.Border, BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(12, 8), Child = hint, [DockPanel.DockProperty] = Dock.Bottom,
        };

        Content = new DockPanel { LastChildFill = true, Children = { searchRow, footer, _status, _scroll } };

        _debounce = new DispatcherTimer { Interval = Debounce };
        _debounce.Tick += (_, _) => { _debounce.Stop(); StartSearch(); };
        _flush = new DispatcherTimer { Interval = FlushEvery };
        _flush.Tick += (_, _) => Flush();

        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        Opened += (_, _) => _search.Focus();
        Closed += (_, _) => { _cts?.Cancel(); _flush.Stop(); _debounce.Stop(); };

        SetStatus($"Type at least {TranscriptSearch.MinQueryLength} characters.");
    }

    /// <summary>The live sessions, so their transcripts sort first and show the live dot.</summary>
    public void SetActiveSessions(IEnumerable<string> sessionIds) => _activeIds = sessionIds.ToHashSet();

    /// <summary>Pre-fill the query (and search it), e.g. from another surface's search box.</summary>
    public void SetQuery(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return;
        _search.Text = query;
        _search.CaretIndex = query.Length;
    }

    private void StyleToggle()
    {
        _toolsToggle.Background = _includeTools ? _p.BrandWash : _p.Raised2;
        _toolsToggle.BorderBrush = _includeTools ? _p.BrandLine : _p.Border;
        _toolsLabel.Foreground = _includeTools ? _p.Title : _p.Muted;
    }

    // ── Searching ────────────────────────────────────────────────────────────────

    // Cancels any running search, clears the results and starts a fresh one for the box's text.
    private void StartSearch()
    {
        _cts?.Cancel();
        _cts = null;
        _flush.Stop();
        ClearResults();

        var query = (_search.Text ?? "").Trim();
        _query = query;
        if (query.Length < TranscriptSearch.MinQueryLength)
        {
            SetStatus(query.Length == 0 ? $"Type at least {TranscriptSearch.MinQueryLength} characters." : "Keep typing…");
            return;
        }

        var cts = new CancellationTokenSource();
        _cts = cts;
        lock (_pendingLock) _pending = new();
        _scanned = 0;
        _total = 0;
        _done = false;
        _clock = System.Diagnostics.Stopwatch.StartNew();
        bool includeTools = _includeTools;
        var active = _activeIds;
        SetStatus("Searching…");
        _flush.Start();

        Task.Run(() =>
        {
            // The same list (and order) as the history viewer: active first, then newest first.
            var entries = SessionHistory.ListAll(active);
            Volatile.Write(ref _total, entries.Count);
            TranscriptSearch.SearchMany(entries.Select(e => e.Path).ToList(), query, includeTools, HitsPerSession,
                (i, m) => { lock (_pendingLock) _pending.Add((i, entries[i], m)); },
                () => Interlocked.Increment(ref _scanned), cts.Token);
        }, cts.Token).ContinueWith(t =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (!ReferenceEquals(_cts, cts)) return;   // superseded by a newer search
                _done = true;
                Flush();
                _flush.Stop();
                if (t.IsFaulted) SetStatus("Search failed — try again.");
            });
        }, TaskScheduler.Default);
    }

    // Drains the results that arrived since the last tick onto the screen, each group slotted into its list
    // position, and refreshes the status line.
    private void Flush()
    {
        if (!IsVisible) return;
        List<(int Index, HistoryEntry Entry, TranscriptMatches Matches)> batch;
        lock (_pendingLock) { batch = _pending; _pending = new(); }

        foreach (var (index, entry, matches) in batch)
        {
            _sessionsFound++;
            _hitsFound += matches.Count;
            if (_groups.Count >= MaxSessions && index > _groups.Keys[^1]) continue;
            var group = Group(entry, matches);
            _groups.Add(index, group);
            _list.Children.Insert(_groups.IndexOfKey(index), group);
            if (_groups.Count > MaxSessions)
            {
                _list.Children.Remove(_groups.Values[^1]);
                _groups.RemoveAt(_groups.Count - 1);
            }
        }
        if (batch.Count > 0) RebuildRowOrder();
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        string found = _sessionsFound == 0 ? "No matches"
            : $"{Plural(_sessionsFound, "session")} · {Plural(_hitsFound, "match", "matches")}";
        if (!_done)
        {
            SetStatus(_total > 0 ? $"Searching… {Volatile.Read(ref _scanned)} of {_total} · {found}" : "Searching…");
            return;
        }
        string scope = _includeTools ? "prompts, replies, tools and thinking" : "prompts and replies";
        string more = _sessionsFound > _groups.Count ? $" · showing the newest {_groups.Count}" : "";
        SetStatus($"{found}{more} · {scope} · {_clock.Elapsed.TotalSeconds:0.0}s");
    }

    private void SetStatus(string text) => _status.Text = text;

    private static string Plural(int n, string one, string? many = null) =>
        n == 1 ? $"1 {one}" : $"{n:N0} {many ?? one + "s"}";

    private void ClearResults()
    {
        _list.Children.Clear();
        _groups.Clear();
        _rows.Clear();
        _sessionsFound = 0;
        _hitsFound = 0;
        _selected = -1;
        _scroll.Offset = default;
    }

    // ── Results ──────────────────────────────────────────────────────────────────

    // One session: a header (live dot, name, cwd, match count · when) and its first few hits.
    private Control Group(HistoryEntry e, TranscriptMatches m)
    {
        var dot = new Border
        {
            Width = 9, Height = 9, CornerRadius = new CornerRadius(4.5),
            Background = e.IsActive ? _p.Brand : Brushes.Transparent,
            BorderBrush = e.IsActive ? Brushes.Transparent : _p.Faint, BorderThickness = new Thickness(1.5),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 10, 0),
            [DockPanel.DockProperty] = Dock.Left,
        };
        string count = m.Capped ? $"{m.Count}+ matches" : Plural(m.Count, "match", "matches");
        var meta = new TextBlock
        {
            FontFamily = _p.Mono, FontSize = 11.5, Foreground = _p.Faint, Text = $"{count} · {e.RelativeTime}",
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0),
            [DockPanel.DockProperty] = Dock.Right,
        };
        var name = new TextBlock
        {
            FontFamily = _p.Body, FontSize = 14, FontWeight = FontWeight.SemiBold, Foreground = _p.Title,
            Text = e.DisplayName, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0),
            [DockPanel.DockProperty] = Dock.Left,
        };
        // The cwd fills what's left; a path keeps its tail (the folder name) and elides the head.
        var path = new TextBlock
        {
            FontFamily = _p.Mono, FontSize = 11.5, Foreground = _p.Muted, Text = e.Cwd,
            TextTrimming = TextTrimming.PrefixCharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
        };
        var header = new DockPanel
        {
            LastChildFill = true, Margin = new Thickness(10, 10, 10, 4),
            Children = { dot, meta, name, path },
        };

        var panel = new StackPanel { Children = { header } };
        foreach (var hit in m.Hits) panel.Children.Add(HitRow(e, hit));
        return new Border
        {
            Child = panel, Margin = new Thickness(0, 0, 0, 6), Tag = e,
            BorderBrush = _p.BorderSoft, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 0, 0, 6),
        };
    }

    // One hit: a kind chip, the excerpt with the match lit, and when it was said.
    private Border HitRow(HistoryEntry e, TranscriptHit hit)
    {
        var (kindText, kindFg, kindBg) = hit.Kind switch
        {
            TranscriptHitKind.Prompt => ("You", _p.Brand, _p.BrandWash),
            TranscriptHitKind.Reply => ("Claude", _p.Text, _p.Raised2),
            TranscriptHitKind.Thinking => ("Thinking", _p.Violet, _p.VioletWash),
            _ => ("Tool", _p.Muted, _p.Raised2),
        };
        var chip = new Border
        {
            CornerRadius = SessionPalette.PillRadius, Background = kindBg, Padding = new Thickness(8, 1),
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 10, 0), MinWidth = 58,
            [DockPanel.DockProperty] = Dock.Left,
            Child = new TextBlock
            {
                Text = kindText, FontFamily = _p.Body, FontSize = 11, FontWeight = FontWeight.SemiBold,
                Foreground = kindFg, HorizontalAlignment = HorizontalAlignment.Center,
            },
        };
        var when = new TextBlock
        {
            FontFamily = _p.Mono, FontSize = 11, Foreground = _p.Faint, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(12, 2, 0, 0), [DockPanel.DockProperty] = Dock.Right,
            Text = hit.Timestamp is { } at ? at.ToString(at.Year == DateTime.Now.Year ? "MMM d, HH:mm" : "MMM d yyyy") : "",
        };

        var snippet = new TextBlock
        {
            FontFamily = _p.Body, FontSize = 13, Foreground = _p.Text, TextWrapping = TextWrapping.Wrap,
            MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var s = hit.Snippet;
        int start = Math.Clamp(s.MatchStart, 0, s.Text.Length);
        int len = Math.Clamp(s.MatchLength, 0, s.Text.Length - start);
        snippet.Inlines!.Add(new Run(s.Text[..start]));
        snippet.Inlines.Add(new Run(s.Text.Substring(start, len))
        {
            FontWeight = FontWeight.Bold, Foreground = _p.Title, Background = _p.BrandWash,
        });
        snippet.Inlines.Add(new Run(s.Text[(start + len)..]));

        var row = new Border
        {
            CornerRadius = SessionPalette.ButtonRadius, Padding = new Thickness(10, 7), Margin = new Thickness(18, 0, 0, 0),
            Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand),
            Child = new DockPanel { LastChildFill = true, Children = { chip, when, snippet } },
        };
        row.PointerEntered += (_, _) => Select(_rows.FindIndex(r => ReferenceEquals(r.Row, row)), scroll: false);
        row.PointerReleased += (_, ev) => { if (ev.InitialPressMouseButton == MouseButton.Left) Choose(e, hit); };
        row.Tag = hit;
        return row;
    }

    // The hit rows in screen order (groups by list index, hits in file order), keeping the selection on the
    // same row when a late group slots in above it.
    private void RebuildRowOrder()
    {
        var current = _selected >= 0 && _selected < _rows.Count ? _rows[_selected].Row : null;
        _rows.Clear();
        foreach (var group in _groups.Values)
        {
            if (group is not Border { Tag: HistoryEntry entry, Child: StackPanel panel }) continue;
            foreach (var child in panel.Children)
                if (child is Border { Tag: TranscriptHit hit } row) _rows.Add((row, entry, hit));
        }
        int keep = current is null ? -1 : _rows.FindIndex(r => ReferenceEquals(r.Row, current));
        Select(keep >= 0 ? keep : _rows.Count > 0 ? 0 : -1, scroll: false);
    }

    private void Select(int index, bool scroll)
    {
        if (index < 0 || index >= _rows.Count) index = -1;
        if (_selected >= 0 && _selected < _rows.Count) _rows[_selected].Row.Background = Brushes.Transparent;
        _selected = index;
        if (index < 0) return;
        _rows[index].Row.Background = _p.Raised2;
        if (scroll) _rows[index].Row.BringIntoView();
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Close();
                e.Handled = true;
                break;
            case Key.Down when _rows.Count > 0:
                Select(Math.Min(_selected + 1, _rows.Count - 1), scroll: true);
                e.Handled = true;
                break;
            case Key.Up when _rows.Count > 0:
                Select(Math.Max(_selected - 1, 0), scroll: true);
                e.Handled = true;
                break;
            case Key.Enter:
                _debounce.Stop();
                if (_selected >= 0 && _selected < _rows.Count) Choose(_rows[_selected].Entry, _rows[_selected].Hit);
                else StartSearch();   // typed and hit Enter before the debounce fired
                e.Handled = true;
                break;
        }
    }

    private void Choose(HistoryEntry entry, TranscriptHit hit) => HitChosen?.Invoke(entry, _query, hit);

    /// <summary>HeadlessRenderer hook: show canned results (no disk scan) so the window can be captured.</summary>
    internal void ShowSampleForRender(string query, IReadOnlyList<(HistoryEntry Entry, TranscriptMatches Matches)> results)
    {
        _search.Text = query;
        _debounce.Stop();
        _query = query;
        ClearResults();
        for (int i = 0; i < results.Count; i++)
        {
            var group = Group(results[i].Entry, results[i].Matches);
            _groups.Add(i, group);
            _list.Children.Add(group);
            _sessionsFound++;
            _hitsFound += results[i].Matches.Count;
        }
        RebuildRowOrder();
        _done = true;
        _clock = System.Diagnostics.Stopwatch.StartNew();
        UpdateStatus();
    }
}

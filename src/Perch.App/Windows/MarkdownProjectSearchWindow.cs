using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Perch.Data;
using Path = System.IO.Path;

namespace Perch.Avalonia.Windows;

/// <summary>
/// The project-wide Markdown quick-open — a VS Code "Go to File" palette scoped to a project's <c>.md</c>
/// files. Opened from the Markdown window's "Search all project files…" button, it shows <b>nothing</b> until
/// you type, then the top <see cref="MaxResults"/> fuzzy matches (<see cref="FuzzyMatch"/>) ranked by name and
/// path, with the matched characters highlighted. ↑/↓ (or Tab) moves, Enter/click picks, Esc dismisses.
///
/// A borderless, centred card modelled on <see cref="SessionSwitcherWindow"/>, themed from the parent window's
/// current <see cref="MarkdownWindow.MdTheme"/> so light/dark tracks the editor. Shown with
/// <c>ShowDialog&lt;string?&gt;</c>: it closes returning the chosen file's absolute path, or null if dismissed.
/// The (bounded, <c>.gitignore</c>-aware) file list is fed in via <see cref="SetFiles"/> — synchronously when
/// the parent already has it cached, otherwise after an off-thread scan lands while the palette shows a
/// "Scanning…" line.
/// </summary>
internal sealed class MarkdownProjectSearchWindow : Window
{
    private const int MaxResults = 10;

    private readonly MarkdownWindow.MdTheme _t;
    private readonly string _cwd;
    private readonly TextBox _search;
    private readonly StackPanel _list;
    private readonly TextBlock _status;   // the loading / empty / no-match line
    private readonly List<Control> _rows = new();
    private readonly IBrush _rowSel;

    private IReadOnlyList<string> _files = [];
    private bool _loading = true;
    private List<(string Rel, FuzzyMatch.Result Match)> _results = new();
    private int _selected;
    private bool _closed;

    public MarkdownProjectSearchWindow(MarkdownWindow.MdTheme theme, string cwd)
    {
        _t = theme;
        _cwd = cwd;
        _rowSel = _t.Selection;

        WindowDecorations = WindowDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        ShowInTaskbar = false;
        CanResize = false;
        Width = 640;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Title = "Search project Markdown";

        _search = new TextBox
        {
            PlaceholderText = "Search all project Markdown by name or path…",
            Background = _t.EditorBg, Foreground = _t.Fg,
            BorderBrush = _t.Separator, BorderThickness = new Thickness(0, 0, 0, 1),
            CornerRadius = new CornerRadius(11, 11, 0, 0), FontSize = 15, Padding = new Thickness(14, 12),
            SelectionBrush = _t.Selection, SelectionForegroundBrush = _t.Fg,
        };
        // Fluent otherwise swaps the field to a near-black background on focus/hover; pin it to EditorBg.
        _search.Resources["TextControlBackgroundFocused"] = _t.EditorBg;
        _search.Resources["TextControlBackgroundPointerOver"] = _t.EditorBg;
        _search.TextChanged += (_, _) => Recompute();

        _list = new StackPanel { Margin = new Thickness(6) };
        var scroll = new ScrollViewer
        {
            Content = _list, MaxHeight = 440,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        _status = new TextBlock
        {
            Foreground = _t.Muted, FontSize = 12.5, Margin = new Thickness(14, 12),
            TextWrapping = TextWrapping.Wrap,
        };

        var stack = new StackPanel { Children = { _search, scroll } };
        Content = new Border
        {
            Background = _t.PaneBg, CornerRadius = new CornerRadius(12),
            BorderBrush = _t.Border, BorderThickness = new Thickness(1.5),
            Child = stack, ClipToBounds = true,
        };

        // Intercept navigation keys before the search box sees them (Enter/Tab especially), so typing still
        // reaches the box but ↑/↓/Enter/Esc drive the list.
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        Opened += (_, _) => _search.Focus();

        Recompute();
    }

    /// <summary>Show the "scanning" state while the project walk runs (used when the parent had no cached
    /// list to hand over synchronously).</summary>
    public void SetLoading()
    {
        if (_closed) return;
        _loading = true;
        Recompute();
    }

    /// <summary>Hand the palette the project's Markdown files (already scanned, <c>.gitignore</c>-aware) and
    /// re-run the current query against them. Safe to call after the window is showing, or after it closed
    /// (a no-op then).</summary>
    public void SetFiles(MarkdownProjectFiles files)
    {
        if (_closed) return;
        _loading = false;
        _files = files.RelativePaths;
        Recompute();
    }

    private void Recompute()
    {
        string q = (_search.Text ?? "").Trim();
        _results = q.Length == 0
            ? new()
            : FuzzyMatch.Rank(q, _files, MaxResults).Select(h => (h.Path, h.Match)).ToList();
        _selected = 0;
        Rebuild(q);
    }

    private void Rebuild(string query)
    {
        _list.Children.Clear();
        _rows.Clear();

        if (_loading)
        {
            _status.Text = "Scanning project…";
            _list.Children.Add(_status);
            return;
        }
        if (query.Length == 0)
        {
            int n = _files.Count;
            _status.Text = n == 0
                ? "No Markdown files found in this project."
                : $"Type to search {n:n0} Markdown file{(n == 1 ? "" : "s")} across the project.";
            _list.Children.Add(_status);
            return;
        }
        if (_results.Count == 0)
        {
            _status.Text = $"No project Markdown matches “{query}”.";
            _list.Children.Add(_status);
            return;
        }

        for (int i = 0; i < _results.Count; i++)
        {
            var row = BuildRow(_results[i].Rel, _results[i].Match, i);
            _rows.Add(row);
            _list.Children.Add(row);
        }
        Highlight();
    }

    // One result row: the file name (matched chars in accent) over the muted directory path (also highlighted).
    private Control BuildRow(string rel, FuzzyMatch.Result match, int index)
    {
        int slash = rel.LastIndexOf('/');
        int nameStart = slash + 1;
        string name = slash >= 0 ? rel[nameStart..] : rel;
        string dir = slash >= 0 ? rel[..nameStart] : "";

        var nameText = new TextBlock { FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis };
        AppendHighlighted(nameText.Inlines!, name, nameStart, match.Positions, _t.Fg);

        var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { nameText } };
        if (dir.Length > 0)
        {
            var dirText = new TextBlock { FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis };
            AppendHighlighted(dirText.Inlines!, dir, 0, match.Positions, _t.Muted);
            textStack.Children.Add(dirText);
        }

        var border = new Border
        {
            Child = textStack, CornerRadius = new CornerRadius(7), Padding = new Thickness(12, 8),
            Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand),
        };
        border.PointerEntered += (_, _) => { _selected = index; Highlight(); };
        border.PointerPressed += (_, _) => Choose(index);
        return border;
    }

    // Emit runs for `text` (which is rel[textStart .. textStart+len]), colouring matched characters (whose
    // absolute indices are in `positions`) with the accent + bold, everything else with `baseColor`.
    private void AppendHighlighted(InlineCollection inlines, string text, int textStart,
        IReadOnlyList<int> positions, IBrush baseColor)
    {
        var hi = new HashSet<int>();
        foreach (var p in positions)
            if (p >= textStart && p < textStart + text.Length)
                hi.Add(p - textStart);

        int i = 0;
        while (i < text.Length)
        {
            bool h = hi.Contains(i);
            int j = i;
            while (j < text.Length && hi.Contains(j) == h)
                j++;
            inlines.Add(new Run(text[i..j])
            {
                Foreground = h ? _t.Accent : baseColor,
                FontWeight = h ? FontWeight.Bold : FontWeight.Normal,
            });
            i = j;
        }
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Close(null);
                e.Handled = true;
                break;
            case Key.Down or Key.Tab when !e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                Move(1);
                e.Handled = true;
                break;
            case Key.Up:
            case Key.Tab when e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                Move(-1);
                e.Handled = true;
                break;
            case Key.Enter:
                Choose(_selected);
                e.Handled = true;
                break;
        }
    }

    private void Move(int delta)
    {
        if (_results.Count == 0) return;
        int n = _results.Count;
        _selected = ((_selected + delta) % n + n) % n;
        Highlight();
    }

    private void Highlight()
    {
        for (int i = 0; i < _rows.Count; i++)
            if (_rows[i] is Border b)
                b.Background = i == _selected ? _rowSel : Brushes.Transparent;
        if (_selected >= 0 && _selected < _rows.Count)
            _rows[_selected].BringIntoView();
    }

    // Resolve the row to an absolute path and close, returning it as the dialog result.
    private void Choose(int index)
    {
        if (index < 0 || index >= _results.Count)
            return;
        var abs = Path.Combine(_cwd, _results[index].Rel.Replace('/', Path.DirectorySeparatorChar));
        Close(abs);
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;   // a late SetFiles from a still-running scan becomes a no-op
        base.OnClosed(e);
    }

    /// <summary>Render/verification seam: seed the file list and a query so a headless capture shows the
    /// populated results (with highlighted matches) rather than the empty prompt.</summary>
    internal void SeedForRender(MarkdownProjectFiles files, string query)
    {
        SetFiles(files);
        _search.Text = query;   // fires TextChanged → Recompute
    }
}

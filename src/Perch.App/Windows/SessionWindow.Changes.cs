using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Perch.Avalonia.Theming;
using Perch.Avalonia.Views;
using Perch.Data;
using Perch.Data.Control;

namespace Perch.Avalonia.Windows;

// The changed-files side panel and its composer toggle (docs/session-file-interactions-plan.md). The panel
// lists the session repo's working-tree changes with git diff line markers; each row is a FileRef (left-click
// opens the diff, right-click offers view/diff/reveal/editor). The toggle sits at the composer's top-right.
internal sealed partial class SessionWindow
{
    private ChangedFilesPanel _changesPanel = null!;
    private Border _changesToggle = null!;
    private TextBlock _changesToggleGlyph = null!;
    private bool _changesOpen;
    private DispatcherTimer? _changesRefreshTimer;

    // The composer's top-right toggle for the changed-files panel. Tints to brand while the panel is open.
    private Border BuildChangesToggle()
    {
        _changesToggleGlyph = new TextBlock
        {
            Text = "◨", FontSize = 21, Foreground = _p.Muted,   // ◨ — a right-side panel
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        var b = new Border
        {
            Width = 30, Height = 30, CornerRadius = SessionPalette.ButtonRadius,   // matches the quick-action glyphs
            Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand), IsVisible = false,   // shown once a session attaches
            Child = _changesToggleGlyph, [ToolTip.TipProperty] = "Changed files  ·  git diff",
        };
        b.PointerEntered += (_, _) => { if (!_changesOpen) b.Background = _p.Raised2; };
        b.PointerExited += (_, _) => { if (!_changesOpen) b.Background = Brushes.Transparent; };
        b.PointerReleased += (_, e) => { if (e.InitialPressMouseButton == MouseButton.Left) ToggleChanges(); };
        return b;
    }

    private ChangedFilesPanel BuildChangesPanel()
    {
        var panel = new ChangedFilesPanel(_p,
            openViewer: p => OpenFileInViewerRequested?.Invoke(p),
            viewDiff: p => ViewFileDiffRequested?.Invoke(p),
            onClose: () => SetChangesOpen(false),
            onRefresh: RefreshChangesNow)
        {
            IsVisible = false,
            [DockPanel.DockProperty] = Dock.Right,
        };
        return panel;
    }

    private void ToggleChanges()
    {
        SetChangesOpen(!_changesOpen);
        if (_changesOpen) RefreshChangesNow();
    }

    private void SetChangesOpen(bool open)
    {
        _changesOpen = open;
        _changesPanel.IsVisible = open;
        _changesToggle.Background = open ? _p.BrandWash : Brushes.Transparent;
        _changesToggleGlyph.Foreground = open ? _p.Brand : _p.Muted;
        InvalidateArrange();   // the docked column appeared/vanished — re-arrange the centre
    }

    private void RefreshChangesNow() => _changesPanel.Refresh(_session?.Cwd ?? _cwd);

    // A tool result landed (a file was likely written): coalesce a refresh while the panel is open.
    private void OnConversationChangedForChanges(ConversationItem item, ConversationChange change)
    {
        if (!_changesOpen) return;
        _changesRefreshTimer ??= BuildChangesTimer();
        _changesRefreshTimer.Stop();
        _changesRefreshTimer.Start();
    }

    private DispatcherTimer BuildChangesTimer()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        t.Tick += (_, _) => { t.Stop(); if (_changesOpen) RefreshChangesNow(); };
        return t;
    }

    /// <summary>
    /// The docked "changed files" list: the session repo's working-tree changes (via
    /// <see cref="GitRepoService.GetChangeStats"/>) with git <c>+</c>/<c>-</c> line markers, each row a
    /// <see cref="FileRef"/> (left-click opens its diff; right-click offers view/diff/reveal/editor). Loads
    /// off the UI thread, guarded by a generation token so a stale load can't overwrite a newer one.
    /// </summary>
    private sealed class ChangedFilesPanel : Border
    {
        private readonly SessionPalette _p;
        private readonly Action<string> _openViewer, _viewDiff;
        private readonly TextBlock _countText, _emptyText;
        private readonly StackPanel _rows;
        private string _cwd = "";
        private int _gen;

        public ChangedFilesPanel(SessionPalette p, Action<string> openViewer, Action<string> viewDiff,
            Action onClose, Action onRefresh)
        {
            _p = p;
            _openViewer = openViewer;
            _viewDiff = viewDiff;

            Width = 300;
            Background = _p.Ground;
            BorderBrush = _p.BorderSoft;
            BorderThickness = new Thickness(1, 0, 0, 0);

            _countText = new TextBlock { FontFamily = _p.Mono, FontSize = 11.5, Foreground = _p.Faint, VerticalAlignment = VerticalAlignment.Center };
            var title = new TextBlock
            {
                Text = "CHANGED FILES", FontFamily = _p.Mono, FontSize = 11, LetterSpacing = 1.1,
                Foreground = _p.Faint, VerticalAlignment = VerticalAlignment.Center,
            };
            var headRight = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 2, HorizontalAlignment = HorizontalAlignment.Right,
                Children = { IconButton("⟳", "Refresh", onRefresh), IconButton("✕", "Hide panel", onClose) },
            };
            var head = new DockPanel
            {
                Children =
                {
                    headRight,
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center, Children = { title, _countText } },
                },
            };
            headRight[DockPanel.DockProperty] = Dock.Right;
            var headBorder = new Border
            {
                Padding = new Thickness(14, 11), BorderBrush = _p.BorderSoft, BorderThickness = new Thickness(0, 0, 0, 1),
                Child = head, [DockPanel.DockProperty] = Dock.Top,
            };

            _rows = new StackPanel { Margin = new Thickness(6, 6, 6, 12) };
            _emptyText = new TextBlock
            {
                Text = "No changes", FontFamily = _p.Mono, FontSize = 12, Foreground = _p.Faint,
                Margin = new Thickness(14, 14, 14, 0), TextWrapping = TextWrapping.Wrap,
            };
            var scroll = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new StackPanel { Children = { _emptyText, _rows } },
            };

            Child = new DockPanel { Children = { headBorder, scroll } };
        }

        /// <summary>Reloads the list for <paramref name="cwd"/> off the UI thread. A blank/non-repo cwd renders
        /// a note; a newer call supersedes an in-flight one (generation token).</summary>
        public void Refresh(string cwd)
        {
            _cwd = cwd;
            int gen = ++_gen;
            if (string.IsNullOrEmpty(cwd)) { Render(null); return; }

            _rows.Children.Clear();
            _emptyText.Text = "Loading…";
            _emptyText.IsVisible = true;
            System.Threading.Tasks.Task.Run(() =>
                GitRepoService.IsRepo(cwd) ? new GitRepoService().GetChangeStats(cwd) : null)
                .ContinueWith(t => Dispatcher.UIThread.Post(() =>
                {
                    if (gen != _gen) return;
                    Render(t.IsCompletedSuccessfully ? t.Result : []);
                }));
        }

        private void Render(IReadOnlyList<GitChangeStat>? stats)
        {
            _rows.Children.Clear();
            if (stats is null)
            {
                _emptyText.Text = string.IsNullOrEmpty(_cwd) ? "No session" : "Not a git repository";
                _emptyText.IsVisible = true;
                _countText.Text = "";
                return;
            }
            if (stats.Count == 0)
            {
                _emptyText.Text = "No changes";
                _emptyText.IsVisible = true;
                _countText.Text = "0 files";
                return;
            }
            _emptyText.IsVisible = false;
            _countText.Text = stats.Count == 1 ? "1 file" : $"{stats.Count} files";
            foreach (var s in stats.OrderBy(s => s.Path, StringComparer.OrdinalIgnoreCase))
                _rows.Children.Add(BuildRow(s));
        }

        private Control BuildRow(GitChangeStat s)
        {
            var abs = System.IO.Path.IsPathRooted(s.Path) || _cwd.Length == 0
                ? s.Path
                : System.IO.Path.Combine(_cwd, s.Path);

            var (letter, color) = KindGlyph(s);
            var badge = new Border
            {
                Width = 18, Height = 18, CornerRadius = new CornerRadius(5), Background = _p.Raised2,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = letter, Foreground = color, FontFamily = _p.Mono, FontSize = 11, FontWeight = FontWeight.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                },
            };

            var name = PathLeaf.Of(s.Path);
            var dir = s.Path.Length > name.Length ? s.Path[..^name.Length] : "";
            var pathText = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            pathText.Inlines = new InlineCollection();
            if (dir.Length > 0) pathText.Inlines.Add(new Run(dir) { Foreground = _p.Faint, FontSize = 12, FontFamily = _p.Mono });
            pathText.Inlines.Add(new Run(name) { Foreground = _p.Title, FontSize = 12.5, FontFamily = _p.Mono });

            var counts = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            if (s.Binary)
                counts.Children.Add(new TextBlock { Text = "bin", FontFamily = _p.Mono, FontSize = 11, Foreground = _p.Faint, VerticalAlignment = VerticalAlignment.Center });
            else
            {
                if (s.Added > 0) counts.Children.Add(new TextBlock { Text = $"+{s.Added}", FontFamily = _p.Mono, FontSize = 11, Foreground = _p.Ok, VerticalAlignment = VerticalAlignment.Center });
                if (s.Removed > 0) counts.Children.Add(new TextBlock { Text = $"−{s.Removed}", FontFamily = _p.Mono, FontSize = 11, Foreground = _p.Err, VerticalAlignment = VerticalAlignment.Center });
            }

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,8,*,8,Auto"), VerticalAlignment = VerticalAlignment.Center };
            grid.Children.Add(badge);
            Grid.SetColumn(pathText, 2);
            grid.Children.Add(pathText);
            Grid.SetColumn(counts, 4);
            grid.Children.Add(counts);

            var row = new Border
            {
                Padding = new Thickness(8, 7), CornerRadius = new CornerRadius(7), Background = Brushes.Transparent,
                Cursor = new Cursor(StandardCursorType.Hand), Child = grid,
            };
            row.PointerEntered += (_, _) => row.Background = _p.Raised2;
            row.PointerExited += (_, _) => row.Background = Brushes.Transparent;
            // Left-click views the diff (the panel's job); the menu still offers View for a Markdown file.
            FileRef.Attach(row, abs, _openViewer, _viewDiff, () => _viewDiff(abs));
            return row;
        }

        // The status letter + hue for a change: green add / untracked, blue-ish modified, red delete, violet
        // rename/copy. Drawn as a coloured letter on a raised chip.
        private (string Letter, IBrush Color) KindGlyph(GitChangeStat s) => s.Kind switch
        {
            GitChangeKind.Added       => (s.Untracked ? "?" : "A", _p.Ok),
            GitChangeKind.Modified    => ("M", _p.Await),
            GitChangeKind.Deleted     => ("D", _p.Err),
            GitChangeKind.Renamed     => ("R", _p.Violet),
            GitChangeKind.Copied      => ("C", _p.Violet),
            GitChangeKind.TypeChanged => ("T", _p.Await),
            GitChangeKind.Unmerged    => ("U", _p.Err),
            _                         => ("•", _p.Muted),
        };

        private Border IconButton(string glyph, string tip, Action onClick)
        {
            var b = new Border
            {
                Width = 24, Height = 24, CornerRadius = SessionPalette.ButtonRadius,
                Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand),
                [ToolTip.TipProperty] = tip,
                Child = new TextBlock
                {
                    Text = glyph, FontSize = 13, Foreground = _p.Muted,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                },
            };
            b.PointerEntered += (_, _) => b.Background = _p.Raised2;
            b.PointerExited += (_, _) => b.Background = Brushes.Transparent;
            b.PointerReleased += (_, e) => { if (e.InitialPressMouseButton == MouseButton.Left) onClick(); };
            return b;
        }
    }
}

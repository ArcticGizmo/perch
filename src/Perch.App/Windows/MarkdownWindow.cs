using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Perch.Avalonia.Theming;
using Perch.Data;

namespace Perch.Avalonia.Windows;

/// <summary>
/// The Markdown viewer/editor. Opened from a session's right-click "Markdown files…" item, it lists the
/// <c>.md</c> files that session produced/referenced plus a <c>.gitignore</c>-respecting tree of the
/// project's Markdown (Phase 3), and renders/edits the selected file with a live split preview (Phase 4).
///
/// A single reused instance via <c>WindowHost.ShowOrFocus</c>; <see cref="Retarget"/> re-points it at a
/// different session without reopening. File IO runs off the UI thread and marshals back guarded by
/// <see cref="Visual.IsVisible"/> and a generation token, so a result arriving after the window closed or
/// was re-pointed is dropped (the <c>StatsWindow</c>/<c>GitTreeWindow</c> idiom). Built entirely in code,
/// themed through <see cref="Palette"/>.
/// </summary>
internal sealed class MarkdownWindow : Window
{
    private static readonly FontFamily Mono = new("Cascadia Code, Consolas, Menlo, monospace");

    private readonly AppSettings _settings;

    private readonly TextBlock _titleText;
    private readonly TextBlock _subText;
    private readonly Border _filePaneHost;     // left: session groups + project tree (Phase 3)
    private readonly TextBlock _filesPlaceholder;
    private readonly Border _editorHost;       // right: source + live preview (Phase 4)
    private readonly TextBlock _editorPlaceholder;

    private string? _cwd;
    private string? _sessionId;
    private bool _isActive;   // the session was working (Running/AwaitingInput) at last Retarget
    // Bumped on every Retarget/close so an in-flight off-thread load knows its results are stale and drops
    // them rather than painting into a window that has moved on.
    private int _gen;

    public MarkdownWindow(AppSettings settings)
    {
        _settings = settings;

        Title = "Markdown";
        Width = 1040;
        Height = 720;
        MinWidth = 720;
        MinHeight = 460;
        Background = Palette.SurfaceSunkenBrush;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        // ── Header: title + subtitle (the targeted directory) ──────────────────────────────────────
        _titleText = new TextBlock
        {
            Text = "Markdown", FontSize = 15, FontWeight = FontWeight.SemiBold,
            Foreground = Palette.TitleBrush, VerticalAlignment = VerticalAlignment.Center,
        };
        _subText = new TextBlock
        {
            Text = "", FontSize = 11.5, Foreground = Palette.MutedBrush, FontFamily = Mono,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var header = new Border
        {
            [DockPanel.DockProperty] = Dock.Top,
            Background = Palette.FormBgBrush,
            BorderBrush = Palette.SeparatorBrush,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(16, 10),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children = { _titleText, _subText },
            },
        };

        // ── Body: file pane | splitter | editor ────────────────────────────────────────────────────
        _filesPlaceholder = new TextBlock
        {
            Text = "Loading Markdown files…", FontSize = 12, Foreground = Palette.MutedBrush,
            Margin = new Thickness(14), TextWrapping = TextWrapping.Wrap,
        };
        _filePaneHost = new Border
        {
            Background = Palette.FormBgBrush,
            BorderBrush = Palette.SeparatorBrush,
            BorderThickness = new Thickness(0, 0, 1, 0),
            Child = _filesPlaceholder,
        };

        _editorPlaceholder = new TextBlock
        {
            Text = "Select a file to view it.", FontSize = 12.5, Foreground = Palette.MutedBrush,
            Margin = new Thickness(18), HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _editorHost = new Border { Background = Palette.SurfaceSunkenBrush, Child = _editorPlaceholder };

        var body = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("300,Auto,*"),
        };
        Grid.SetColumn(_filePaneHost, 0);
        var splitter = new GridSplitter
        {
            Width = 4, Background = Palette.SeparatorBrush,
            ResizeDirection = GridResizeDirection.Columns,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        Grid.SetColumn(splitter, 1);
        Grid.SetColumn(_editorHost, 2);
        body.Children.Add(_filePaneHost);
        body.Children.Add(splitter);
        body.Children.Add(_editorHost);

        Content = new DockPanel { LastChildFill = true, Children = { header, body } };
    }

    /// <summary>
    /// Re-point the window at a session's working directory and reload. Called on first open and every
    /// reuse via <c>WindowHost.ShowOrFocus</c>. <paramref name="isActive"/> is true when the session may
    /// still be writing to these files (Running/AwaitingInput), which the editor uses to guard overwrites.
    /// </summary>
    public void Retarget(string cwd, string sessionId, string displayName, bool isActive)
    {
        _cwd = cwd;
        _sessionId = sessionId;
        _isActive = isActive;
        _gen++;   // invalidate any in-flight load from a previous target

        Title = $"Markdown — {displayName}";
        _titleText.Text = displayName;
        _subText.Text = cwd;

        Reload();
    }

    // Phase 3 fills this: scan the session's produced/referenced .md sets and the project .md tree off the
    // UI thread, then populate _filePaneHost guarded by IsVisible + the generation token.
    private void Reload()
    {
        _filesPlaceholder.Text = "Loading Markdown files…";
    }

    protected override void OnClosed(EventArgs e)
    {
        _gen++;   // drop any results still in flight
        base.OnClosed(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }
}

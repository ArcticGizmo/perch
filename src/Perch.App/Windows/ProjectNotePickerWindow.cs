using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Perch.Avalonia.Theming;
using Perch.Data;

namespace Perch.Avalonia.Windows;

/// <summary>
/// A searchable list of every project Perch knows about, opened by right-clicking the overlay's note
/// button. Pick one — even a project with no live session — to add or edit its <em>project</em> note, so a
/// reminder can be attached to something you're not actively working on without first starting a Claude
/// session. Projects already carrying a note are flagged (green dot) and preview it; the list is ordered
/// most-recently-active first and filters live as you type. Selecting a row raises <see cref="ProjectChosen"/>
/// and closes.
/// <para>
/// Deliberately modelled on <see cref="SessionSwitcherWindow"/> — the same borderless card, flush search
/// header, list, and footer-hint chrome — so Perch's two "type-to-find" surfaces read as one tool. Owned by
/// the overlay so it stays above the always-on-top panel; a normal activatable window, so the search box
/// takes focus and clicking away dismisses it.
/// </para>
/// </summary>
internal sealed class ProjectNotePickerWindow : Window
{
    private static readonly IBrush CardBg   = Palette.OverlaySurfaceBrush;
    private static readonly IBrush Stroke   = Palette.BorderBrush;
    private static readonly IBrush SearchBg = Palette.FormBgBrush;
    private static readonly IBrush RowSel    = new SolidColorBrush(Color.FromRgb(40, 44, 62)); // selection wash
    private static Color NoteDot => Palette.Green; // green = has a project note, matching the sticky note's tab

    private const string HintText = "↵  add / edit note        Esc  close";

    private readonly IReadOnlyList<ProjectEntry> _all;
    private readonly TextBox _search;
    private readonly StackPanel _list = new() { Margin = new Thickness(6) };
    private readonly List<Control> _rows = new();

    private List<ProjectEntry> _filtered = new();
    private int _selected;
    private bool _chosen;
    private bool _ready; // armed once focus settles, so opening can't self-dismiss on a transient deactivation

    /// <summary>Raised with the chosen project when the user picks a row (click or Enter). The window has
    /// already closed by the time it fires.</summary>
    public event Action<ProjectEntry>? ProjectChosen;

    public ProjectNotePickerWindow(IReadOnlyList<ProjectEntry> projects)
    {
        _all = projects;

        WindowDecorations = WindowDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Topmost = true;
        ShowInTaskbar = false;
        CanResize = false;
        Width = 560;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        // The search header — same chrome as the session switcher: a flush, full-width field with rounded
        // top corners matching the card and a single hairline beneath it.
        _search = new TextBox
        {
            PlaceholderText = "Add a note to a project…",
            Background = SearchBg, Foreground = Palette.FgBrush,
            BorderBrush = Stroke, BorderThickness = new Thickness(0, 0, 0, 1),
            CornerRadius = new CornerRadius(11, 11, 0, 0), FontSize = 16, Padding = new Thickness(14, 12),
        };
        _search.TextChanged += (_, _) => ApplyFilter();

        var scroll = new ScrollViewer
        {
            Content = _list, MaxHeight = 380,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        var hint = new TextBlock
        {
            Text = HintText, Foreground = Palette.MutedBrush, FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var footer = new Border
        {
            BorderBrush = Stroke, BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(12, 9), Child = hint,
        };

        Content = new Border
        {
            Background = CardBg, CornerRadius = new CornerRadius(12),
            BorderBrush = Stroke, BorderThickness = new Thickness(1.5),
            Child = new StackPanel { Children = { _search, scroll, footer } },
            ClipToBounds = true,
        };

        // Intercept navigation keys before the search box consumes them (Up/Down aren't used by a single-
        // line TextBox, but Enter is — the tunnel handler wins so typing still reaches the box).
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);

        Opened += (_, _) =>
        {
            _search.Focus();
            // Arm dismiss-on-deactivate only after this batch settles, so grabbing focus on open can't trip
            // an immediate self-close.
            Dispatcher.UIThread.Post(() => _ready = true, DispatcherPriority.Background);
        };
        Deactivated += (_, _) => { if (_ready && !_chosen) Close(); };

        ApplyFilter();
    }

    // Rebuilds the visible list from the current search text. Matches project name or full path (so you can
    // search by folder), keeps the ordering the catalog gave us, and re-homes the selection on the first row.
    private void ApplyFilter()
    {
        var q = _search.Text?.Trim() ?? "";
        _filtered = q.Length == 0
            ? _all.ToList()
            : _all.Where(p =>
                p.ProjectName.Contains(q, StringComparison.OrdinalIgnoreCase)
                || p.Cwd.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        _selected = 0;
        Rebuild();
    }

    private void Rebuild()
    {
        _list.Children.Clear();
        _rows.Clear();

        if (_filtered.Count == 0)
        {
            _list.Children.Add(new TextBlock
            {
                Text = _all.Count == 0
                    ? "No projects yet. Start a Claude session in a folder and it'll show up here."
                    : "No matching projects",
                Foreground = Palette.MutedBrush, FontSize = 13,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 14),
            });
            return;
        }

        for (int i = 0; i < _filtered.Count; i++)
        {
            var row = BuildRow(_filtered[i], i);
            _rows.Add(row);
            _list.Children.Add(row);
        }
        Highlight();
    }

    private Control BuildRow(ProjectEntry p, int index)
    {
        // Filled green dot marks a project that already has a note; a hollow dot marks a fresh one.
        var dot = new Ellipse
        {
            Width = 10, Height = 10, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 12, 0),
        };
        if (p.HasNote)
        {
            dot.Fill = new SolidColorBrush(NoteDot);
        }
        else
        {
            dot.Fill = Brushes.Transparent;
            dot.Stroke = new SolidColorBrush(Palette.Idle);
            dot.StrokeThickness = 1.5;
        }

        var name = new TextBlock
        {
            Text = p.ProjectName, Foreground = Palette.TitleBrush, FontSize = 14, FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        // Secondary line: the note preview when there is one, otherwise the working directory.
        var subtitle = new TextBlock
        {
            Text = p.HasNote ? FirstLine(p.Note!) : p.Cwd,
            Foreground = Palette.MutedBrush, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var textStack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center, Children = { name, subtitle },
        };
        Grid.SetColumn(dot, 0);
        Grid.SetColumn(textStack, 1);

        var meta = new TextBlock
        {
            Text = SessionHistory.Relative(p.LastActivity), Foreground = Palette.MutedBrush, FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        Grid.SetColumn(meta, 2);

        // Trailing affordance: a pencil, echoing the switcher's →/↻ glyphs.
        var glyph = new TextBlock
        {
            Text = "✎", Foreground = Palette.MutedBrush, FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 2, 0),
        };
        Grid.SetColumn(glyph, 3);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto") };
        grid.Children.Add(dot);
        grid.Children.Add(textStack);
        grid.Children.Add(meta);
        grid.Children.Add(glyph);

        var border = new Border
        {
            Child = grid, CornerRadius = new CornerRadius(7), Padding = new Thickness(12, 9),
            Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand),
        };
        ToolTip.SetTip(border, p.Cwd);
        border.PointerEntered += (_, _) => { _selected = index; Highlight(); };
        border.PointerPressed += (_, _) => Pick(_filtered[index]);
        return border;
    }

    private void Highlight()
    {
        for (int i = 0; i < _rows.Count; i++)
            if (_rows[i] is Border b)
                b.Background = i == _selected ? RowSel : Brushes.Transparent;
        if (_selected >= 0 && _selected < _rows.Count)
            _rows[_selected].BringIntoView();
    }

    private void Move(int delta)
    {
        if (_filtered.Count == 0) return;
        int n = _filtered.Count;
        _selected = ((_selected + delta) % n + n) % n;
        Highlight();
    }

    private void Pick(ProjectEntry chosen)
    {
        if (_chosen) return;
        _chosen = true;
        Close();
        ProjectChosen?.Invoke(chosen);
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape: Close(); e.Handled = true; break;
            case Key.Down or Key.Tab when !e.KeyModifiers.HasFlag(KeyModifiers.Shift): Move(1); e.Handled = true; break;
            case Key.Up: Move(-1); e.Handled = true; break;
            case Key.Tab when e.KeyModifiers.HasFlag(KeyModifiers.Shift): Move(-1); e.Handled = true; break;
            case Key.Enter:
                if (_filtered.Count > 0 && _selected >= 0 && _selected < _filtered.Count) Pick(_filtered[_selected]);
                e.Handled = true;
                break;
        }
    }

    private static string FirstLine(string text)
    {
        int nl = text.IndexOfAny(['\n', '\r']);
        return nl < 0 ? text : text[..nl];
    }
}

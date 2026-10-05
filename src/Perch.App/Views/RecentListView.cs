using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Perch.Avalonia.Theming;
using RecentLine = Perch.Avalonia.Views.OverlayCanvas.RecentLine;
using RecentTone = Perch.Avalonia.Views.OverlayCanvas.RecentTone;

namespace Perch.Avalonia.Views;

/// <summary>Which Recent lines the list shows.</summary>
internal enum RecentFilter
{
    /// <summary>Every session that ended lately.</summary>
    All,
    /// <summary>Cut off rather than ended: a restart or crash killed it, or Perch had it open when Perch closed.</summary>
    Interrupted,
    /// <summary>Ended (any way) just before a shutdown.</summary>
    BeforeShutdown,
}

/// <summary>
/// The body of the overlay's Recent flyout (the clock button on the "+ New session" row; docs/session-recovery-plan.md):
/// a "Recent" caption with "Show all…" (the launcher's full list), a filter chip per <see cref="RecentFilter"/> with its
/// count, then the matching lines. A click on a line resumes it in Perch (dormant); hovering a line swaps its note for
/// "resume in a terminal" and "dismiss" buttons. The lines arrive precomputed (<see cref="SetLines"/>); a line's
/// filters are its <see cref="RecentLine.Interrupted"/>/<see cref="RecentLine.BeforeShutdown"/> flags, so one that a
/// restart cut off shows under both.
/// </summary>
internal sealed class RecentListView : StackPanel
{
    private const double ListWidth = 320;

    private readonly StackPanel _chips = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    private readonly StackPanel _rows = new() { Spacing = 1 };
    private IReadOnlyList<RecentLine> _lines = [];
    private RecentFilter _filter;

    public event Action<string, string>? ResumeRequested;
    public event Action<string, string>? TerminalRequested;
    public event Action<string>? DismissRequested;
    public event Action? ShowAllRequested;
    public event Action<RecentFilter>? FilterChanged;

    public RecentListView(RecentFilter filter)
    {
        _filter = filter;
        Width = ListWidth;
        Spacing = 8;

        var showAll = new TextBlock
        {
            Text = "Show all…", FontSize = 11.5, Foreground = Palette.MutedBrush, Cursor = new Cursor(StandardCursorType.Hand),
            VerticalAlignment = VerticalAlignment.Center,
        };
        showAll.PointerEntered += (_, _) => showAll.Foreground = Palette.AccentBrush;
        showAll.PointerExited += (_, _) => showAll.Foreground = Palette.MutedBrush;
        showAll.PointerReleased += (_, e) => { if (e.InitialPressMouseButton == MouseButton.Left) ShowAllRequested?.Invoke(); };
        var header = new DockPanel { Margin = new Thickness(2, 0) };
        DockPanel.SetDock(showAll, Dock.Right);
        header.Children.Add(showAll);
        header.Children.Add(new TextBlock { Text = "Recent", FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = Palette.FgBrush });

        Children.Add(header);
        Children.Add(_chips);
        Children.Add(new ScrollViewer
        {
            MaxHeight = 360, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _rows,
        });
    }

    public RecentFilter Filter => _filter;

    /// <summary>Replaces the lines (an open list follows the app's Recent pushes, a dismissal included).</summary>
    public void SetLines(IReadOnlyList<RecentLine> lines)
    {
        _lines = lines;
        Rebuild();
    }

    internal static bool Matches(RecentLine l, RecentFilter f) => f switch
    {
        RecentFilter.Interrupted => l.Interrupted,
        RecentFilter.BeforeShutdown => l.BeforeShutdown,
        _ => true,
    };

    private void Rebuild()
    {
        _chips.Children.Clear();
        foreach (var f in new[] { RecentFilter.All, RecentFilter.Interrupted, RecentFilter.BeforeShutdown })
            _chips.Children.Add(Chip(f, _lines.Count(l => Matches(l, f))));

        _rows.Children.Clear();
        foreach (var l in _lines)
            if (Matches(l, _filter)) _rows.Children.Add(Row(l));
        if (_rows.Children.Count == 0)
            _rows.Children.Add(new TextBlock
            {
                Text = _filter switch
                {
                    RecentFilter.Interrupted => "Nothing interrupted lately",
                    RecentFilter.BeforeShutdown => "Nothing ended just before a shutdown",
                    _ => "Nothing recent",
                },
                FontSize = 12, Foreground = Palette.MutedBrush, Margin = new Thickness(6, 6),
            });
    }

    private Control Chip(RecentFilter f, int count)
    {
        bool on = f == _filter;
        string name = f switch { RecentFilter.Interrupted => "Interrupted", RecentFilter.BeforeShutdown => "Before shutdown", _ => "All" };
        var label = new TextBlock
        {
            Text = $"{name} {count}", FontSize = 11.5,
            Foreground = on ? Palette.OnAccentBrush : Palette.MutedBrush,
        };
        var chip = new Border
        {
            CornerRadius = new CornerRadius(10), Padding = new Thickness(9, 3),
            Background = on ? Palette.AccentBrush : Palette.ButtonBgBrush,
            Cursor = new Cursor(StandardCursorType.Hand), Child = label,
        };
        if (!on)
        {
            chip.PointerEntered += (_, _) => label.Foreground = Palette.FgBrush;
            chip.PointerExited += (_, _) => label.Foreground = Palette.MutedBrush;
        }
        chip.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton != MouseButton.Left || _filter == f) return;
            _filter = f;
            FilterChanged?.Invoke(f);
            Rebuild();
        };
        return chip;
    }

    private Control Row(RecentLine l)
    {
        var (dot, noteBrush) = l.Tone switch
        {
            RecentTone.Flagged => (Palette.WarnBrush, Palette.WarnBrush),
            RecentTone.Perch => (Palette.AccentBrush, Palette.AccentBrush),
            RecentTone.Faded => (Palette.MutedBrush, Palette.MutedBrush),
            _ => (Palette.TeamGrayBrush, Palette.MutedBrush),
        };
        var title = new TextBlock
        {
            Text = l.Title, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
            Foreground = l.Tone == RecentTone.Faded ? Palette.MutedBrush : Palette.FgBrush,
        };
        // The title gives way first: the folder's name stays legible beside it.
        var name = new DockPanel { VerticalAlignment = VerticalAlignment.Center };
        if (l.Folder is { Length: > 0 } folder)
        {
            var folderText = new TextBlock
            {
                Text = folder, FontSize = 11, Foreground = Palette.MutedBrush, MaxWidth = 110, Margin = new Thickness(6, 0, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
            };
            DockPanel.SetDock(folderText, Dock.Right);
            name.Children.Add(folderText);
        }
        name.Children.Add(title);

        var note = new TextBlock
        {
            Text = l.Note, FontSize = 11, Foreground = noteBrush, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 2, Opacity = 0, IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(8, 0, 0, 0),
            Children =
            {
                HoverButton(">_", "Resume in terminal", () => TerminalRequested?.Invoke(l.SessionId, l.Cwd)),
                HoverButton("×", "Dismiss", () => DismissRequested?.Invoke(l.SessionId)),
            },
        };
        // The note and the hover actions share the right-hand cell, which is sized for both (the hidden one is only
        // transparent), so swapping them never reflows the title.
        note.HorizontalAlignment = HorizontalAlignment.Right;
        var trailing = new Grid { Children = { note, actions } };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        grid.Children.Add(new Ellipse { Width = 6, Height = 6, Fill = dot, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);
        Grid.SetColumn(trailing, 2);
        grid.Children.Add(trailing);

        var row = new Border
        {
            CornerRadius = new CornerRadius(5), Padding = new Thickness(6, 0), MinHeight = 26, Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand), Child = grid,
        };
        ToolTip.SetTip(row, l.Cwd);
        void Hover(bool on)
        {
            row.Background = on ? Palette.OverlayRowHoverBrush : Brushes.Transparent;
            note.Opacity = on ? 0 : 1;
            actions.Opacity = on ? 1 : 0;
            actions.IsHitTestVisible = on;
        }
        row.PointerEntered += (_, _) => Hover(true);
        row.PointerExited += (_, _) => Hover(false);
        row.PointerReleased += (_, e) => { if (e.InitialPressMouseButton == MouseButton.Left) ResumeRequested?.Invoke(l.SessionId, l.Cwd); };
        return row;
    }

    // A small hover button in a row's trailing cell. It handles its own release, so the row's "resume" doesn't fire too.
    private static Control HoverButton(string glyph, string tip, Action onClick)
    {
        var text = new TextBlock
        {
            Text = glyph, FontSize = 11.5, Foreground = Palette.MutedBrush,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        var box = new Border
        {
            Width = 22, Height = 20, CornerRadius = new CornerRadius(4), Background = Brushes.Transparent, Child = text,
        };
        ToolTip.SetTip(box, tip);
        box.PointerEntered += (_, _) => { box.Background = Palette.ButtonBgBrush; text.Foreground = Palette.FgBrush; };
        box.PointerExited += (_, _) => { box.Background = Brushes.Transparent; text.Foreground = Palette.MutedBrush; };
        box.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton != MouseButton.Left) return;
            e.Handled = true;
            onClick();
        };
        return box;
    }
}

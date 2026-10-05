using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Perch.Avalonia.Theming;
using Perch.Data;

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

/// <summary>The brushes (and font) a <see cref="RecentListView"/> is drawn with: the overlay's <see cref="Palette"/>, or
/// the Roost's <see cref="SessionPalette"/> (<see cref="For(SessionPalette)"/>).</summary>
internal sealed record RecentListLook(
    IBrush Text, IBrush Muted, IBrush ChipOn, IBrush ChipOnText, IBrush ChipOff, IBrush RowHover, IBrush ButtonHover,
    IBrush Warn, IBrush Perch, IBrush Normal, FontFamily? Font = null)
{
    public static RecentListLook Overlay => new(
        Palette.FgBrush, Palette.MutedBrush, Palette.AccentBrush, Palette.OnAccentBrush, Palette.ButtonBgBrush,
        Palette.OverlayRowHoverBrush, Palette.ButtonBgBrush, Palette.WarnBrush, Palette.AccentBrush, Palette.TeamGrayBrush);

    // The Roost's rail idiom: a flagged ending in the awaiting hue, the selected chip like its Status | A–Z toggle.
    public static RecentListLook For(SessionPalette p) => new(
        p.Text, p.Muted, p.BrandWash, p.Text, p.Raised2, p.Raised2, p.Border, p.Await, p.Brand, p.Idle, p.Body);
}

/// <summary>
/// The body of a Recent flyout — the overlay's clock button on the "+ New session" row, and the Roost rail's "Recent"
/// footer row (docs/session-recovery-plan.md): a "Recent" caption with an optional "Show all…" (the launcher's full
/// list), a filter chip per <see cref="RecentFilter"/> with its count, then the matching lines. A click on a line opens
/// it; hovering a line swaps its note for "resume in a terminal" and "dismiss" buttons. The lines arrive precomputed
/// (<see cref="SetLines"/>); a line's filters are its <see cref="RecentLine.Interrupted"/>/
/// <see cref="RecentLine.BeforeShutdown"/> flags, so one that a restart cut off shows under both.
/// </summary>
internal sealed class RecentListView : StackPanel
{
    private const double ListWidth = 320;
    private static readonly Cursor Hand = new(StandardCursorType.Hand);

    private readonly RecentListLook _look;
    private readonly StackPanel _chips = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    private readonly StackPanel _rows = new() { Spacing = 1 };
    private IReadOnlyList<RecentLine> _lines = [];
    private RecentFilter _filter;

    public event Action<string, string>? ResumeRequested;
    public event Action<string, string>? TerminalRequested;
    public event Action<string>? DismissRequested;
    public event Action? ShowAllRequested;
    public event Action<RecentFilter>? FilterChanged;

    public RecentListView(RecentFilter filter, RecentListLook? look = null, bool showAll = true)
    {
        _filter = filter;
        _look = look ?? RecentListLook.Overlay;
        Width = ListWidth;
        Spacing = 8;
        if (_look.Font is { } font) SetValue(TextElement.FontFamilyProperty, font);

        var header = new DockPanel { Margin = new Thickness(2, 0) };
        if (showAll)
        {
            var link = new TextBlock
            {
                Text = "Show all…", FontSize = 11.5, Foreground = _look.Muted, Cursor = Hand,
                VerticalAlignment = VerticalAlignment.Center,
            };
            link.PointerEntered += (_, _) => link.Foreground = _look.Text;
            link.PointerExited += (_, _) => link.Foreground = _look.Muted;
            link.OnLeftClick(() => ShowAllRequested?.Invoke());
            DockPanel.SetDock(link, Dock.Right);
            header.Children.Add(link);
        }
        header.Children.Add(new TextBlock { Text = "Recent", FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = _look.Text });

        Children.Add(header);
        Children.Add(_chips);
        Children.Add(new ScrollViewer
        {
            MaxHeight = 360, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _rows,
        });
        Rebuild();
    }

    /// <summary>Replaces the lines (an open list follows its host's refreshes, a dismissal included). Unchanged lines
    /// keep the rows as they are, hover and all.</summary>
    public void SetLines(IReadOnlyList<RecentLine> lines)
    {
        if (lines.SequenceEqual(_lines)) return;
        _lines = lines;
        Rebuild();
    }

    private static bool Matches(RecentLine l, RecentFilter f) => f switch
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
                FontSize = 12, Foreground = _look.Muted, Margin = new Thickness(6, 6),
            });
    }

    private Control Chip(RecentFilter f, int count)
    {
        bool on = f == _filter;
        string name = f switch { RecentFilter.Interrupted => "Interrupted", RecentFilter.BeforeShutdown => "Before shutdown", _ => "All" };
        var label = new TextBlock
        {
            Text = $"{name} {count}", FontSize = 11.5,
            Foreground = on ? _look.ChipOnText : _look.Muted,
        };
        var chip = new Border
        {
            CornerRadius = new CornerRadius(10), Padding = new Thickness(9, 3),
            Background = on ? _look.ChipOn : _look.ChipOff,
            Cursor = Hand, Child = label,
        };
        if (on) return chip;
        chip.PointerEntered += (_, _) => label.Foreground = _look.Text;
        chip.PointerExited += (_, _) => label.Foreground = _look.Muted;
        return chip.OnLeftClick(() =>
        {
            _filter = f;
            FilterChanged?.Invoke(f);
            Rebuild();
        });
    }

    private Control Row(RecentLine l)
    {
        var (dot, noteBrush) = l.Tone switch
        {
            RecentTone.Flagged => (_look.Warn, _look.Warn),
            RecentTone.Perch => (_look.Perch, _look.Perch),
            RecentTone.Faded => (_look.Muted, _look.Muted),
            _ => (_look.Normal, _look.Muted),
        };
        var title = new TextBlock
        {
            Text = l.Title, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
            Foreground = l.Tone == RecentTone.Faded ? _look.Muted : _look.Text,
        };
        // The title gives way first: the folder's name stays legible beside it.
        var name = new DockPanel { VerticalAlignment = VerticalAlignment.Center };
        if (l.Folder is { Length: > 0 } folder)
        {
            var folderText = new TextBlock
            {
                Text = folder, FontSize = 11, Foreground = _look.Muted, MaxWidth = 110, Margin = new Thickness(6, 0, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
            };
            DockPanel.SetDock(folderText, Dock.Right);
            name.Children.Add(folderText);
        }
        name.Children.Add(title);

        var note = new TextBlock
        {
            Text = l.Note, FontSize = 11, Foreground = noteBrush, VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(8, 0, 0, 0),
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
            Cursor = Hand, Child = grid,
        };
        ToolTip.SetTip(row, l.Cwd);
        void Hover(bool on)
        {
            row.Background = on ? _look.RowHover : Brushes.Transparent;
            note.Opacity = on ? 0 : 1;
            actions.Opacity = on ? 1 : 0;
            actions.IsHitTestVisible = on;
        }
        row.PointerEntered += (_, _) => Hover(true);
        row.PointerExited += (_, _) => Hover(false);
        return row.OnLeftClick(() => ResumeRequested?.Invoke(l.SessionId, l.Cwd));
    }

    // A small hover button in a row's trailing cell. It handles its own release, so the row's "open" doesn't fire too.
    private Control HoverButton(string glyph, string tip, Action onClick)
    {
        var text = new TextBlock
        {
            Text = glyph, FontSize = 11.5, Foreground = _look.Muted,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        var box = new Border
        {
            Width = 22, Height = 20, CornerRadius = new CornerRadius(4), Background = Brushes.Transparent, Child = text,
        };
        ToolTip.SetTip(box, tip);
        box.PointerEntered += (_, _) => { box.Background = _look.ButtonHover; text.Foreground = _look.Text; };
        box.PointerExited += (_, _) => { box.Background = Brushes.Transparent; text.Foreground = _look.Muted; };
        return box.OnLeftClick(onClick, handle: true);
    }
}

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Perch.Avalonia.Theming;

namespace Perch.Avalonia.Windows;

/// <summary>
/// A free-form note editor styled — and behaving — like a paper sticky note. Serves two callers:
/// <list type="bullet">
///   <item>the <b>global</b> scratch pad (<see cref="Global"/>), opened from the note button leading the
///   quick-links row — a single free-text pad; and</item>
///   <item>a <b>row note</b> (<see cref="ForSessionRow"/>), opened from a session's right-click menu — two
///   stacked sections, one for a <em>project</em> note shared by every session in the same working
///   directory and one for a note pinned to just <em>this session</em>. Twice the single-note height so
///   both fit comfortably.</item>
/// </list>
/// Unlike the old modal editor it is <b>non-modal and owned by the overlay</b>: it stays open like a real
/// sticky note while you keep clicking around Perch, and because it's owned it can never fall <em>behind</em>
/// the always-on-top overlay. It also positions itself <em>beside</em> the overlay — on whichever side has
/// the most room — so it never lands on top of it. Save persists via the supplied callback; closing with
/// unsaved edits first asks to confirm, so a stray Esc can't silently drop a note.
/// </summary>
internal sealed class StickyNoteWindow : Window
{
    private const double NoteWidth = 442;      // ~30% larger than the original 340
    private const double SingleHeight = 390;   // one section (~30% up from 300); a row note doubles this
    private const int ShadowRoom = 16;         // transparent margin around the paper so its shadow shows
    private const int SideGap = 16;            // gap between the overlay and the note when placed beside it
    private const int CascadeStep = 26;        // per-open offset so stacked notes don't hide each other

    private static readonly FontFamily HandFont = new("Segoe Print, Comic Sans MS, Segoe UI");
    private static readonly FontFamily MonoFont = new("Consolas, Cascadia Mono, monospace");

    private readonly IReadOnlyList<TextBox> _boxes;
    private readonly string[] _initial;
    private Action _persist = () => { };       // reads the boxes and writes them through the caller's sink
    private bool _saving;                      // Save pressed — skip the discard confirmation
    private bool _closeConfirmed;              // discard already confirmed — let the re-issued Close through

    /// <summary>How many notes are already open, so this one can cascade instead of stacking exactly on
    /// top of the last. Set by the caller before <see cref="Window.Show()"/>.</summary>
    public int CascadeIndex { get; set; }

    // A note "scope": a coloured tab + heading over an editable paper area.
    private sealed record Section(string Heading, string? Initial, string Placeholder, Color Accent);

    // Paper + ink tints. The whole note is one warm sticky; the two row sections are tinted cards on it.
    private static readonly Color Paper      = Color.FromRgb(0xFF, 0xF2, 0x9B); // sticky yellow
    private static readonly Color PaperEdge  = Color.FromRgb(0xEC, 0xD5, 0x6B);
    private static readonly Color Ink        = Color.FromRgb(0x3A, 0x32, 0x14);
    private static readonly Color InkMuted   = Color.FromRgb(0x7A, 0x6C, 0x38);
    private static readonly Color ProjectAccent = Color.FromRgb(0x2E, 0x9E, 0x5B); // green tab = project
    private static readonly Color SessionAccent = Color.FromRgb(0x3D, 0x7E, 0xC4); // blue tab  = session

    /// <summary>The global scratch pad: one free-text section. <paramref name="onSave"/> receives the
    /// (trimmed) pad text on Save (empty = clear).</summary>
    public static StickyNoteWindow Global(string? existingText, Action<string> onSave)
    {
        var w = new StickyNoteWindow(
            "Scratch pad", SingleHeight,
            [new Section(
                "", // the window title already says "Scratch pad" — no in-body heading needed
                existingText, "Jot anything here…", SessionAccent)]);
        w.SetPersist(() => onSave(w.SectionText(0)));
        return w;
    }

    /// <summary>A session row's note: a project section over a session section, at double height.
    /// <paramref name="onSave"/> receives (projectText, sessionText), each trimmed (empty = clear).</summary>
    public static StickyNoteWindow ForSessionRow(
        string sessionDisplayName, string projectName, string? projectText, string? sessionText,
        Action<string, string> onSave)
    {
        var w = new StickyNoteWindow(
            $"Notes — {sessionDisplayName}", SingleHeight * 2,
            [
                new Section(
                    $"Project · {projectName}",
                    projectText, "Shared by every session in this project…", ProjectAccent),
                new Section(
                    "This session",
                    sessionText, "Pinned to this session alone…", SessionAccent),
            ]);
        w.SetPersist(() => onSave(w.SectionText(0), w.SectionText(1)));
        return w;
    }

    /// <summary>
    /// Builds a detached, fixed-size copy of the note surface for the headless render harness — no owner,
    /// no live positioning, no persistence. Not used at runtime. <paramref name="sessionRow"/> picks the
    /// two-section row note; otherwise the single-section global pad.
    /// </summary>
    internal static Control BuildPreviewSurface(bool sessionRow)
    {
        var w = sessionRow
            ? ForSessionRow(
                "api", "perch",
                "Freeze main before the release — ping #eng when the tag is cut.",
                "Risky refactor — mid-bisect on a flaky test. Don't rebase.",
                (_, _) => { })
            : Global("Ship v0.9:\n- cut the tag\n- update the changelog\n- poke the CI flake", _ => { });
        var content = (Control)w.Content!;
        w.Content = null;                 // detach so the panel can adopt it
        content.Width = NoteWidth;
        content.Height = w.Height;
        return content;
    }

    /// <summary>The trimmed text of section <paramref name="index"/> ("" when empty or out of range).</summary>
    public string SectionText(int index) =>
        index >= 0 && index < _boxes.Count ? _boxes[index].Text?.Trim() ?? "" : "";

    // Installs the sink the factory built (a closure over this window's section text + the caller's action).
    private void SetPersist(Action persist) => _persist = persist;

    private StickyNoteWindow(string title, double height, IReadOnlyList<Section> sections)
    {
        Title = title;
        Width = NoteWidth;
        Height = height;
        MinWidth = 260;
        MinHeight = 200;
        CanResize = true;
        ShowInTaskbar = false;
        WindowDecorations = WindowDecorations.None;          // borderless — it's a sticky note, not a window
        Background = Brushes.Transparent;                    // let the paper (with its shadow) be the shape
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        WindowStartupLocation = WindowStartupLocation.Manual; // placed beside the overlay in OnOpened

        // Keep the editor unmistakably paper: override Fluent's default (near-black) focus/hover fills so a
        // focused field just deepens to a darker yellow, and the focus underline stays amber, not blue.
        Resources["TextControlBackground"]            = Brushes.Transparent;
        Resources["TextControlBackgroundPointerOver"] = new SolidColorBrush(Color.FromRgb(0xF7, 0xE6, 0x84));
        Resources["TextControlBackgroundFocused"]     = new SolidColorBrush(Color.FromRgb(0xEC, 0xD5, 0x5A));
        Resources["TextControlBorderBrush"]            = Brushes.Transparent;
        Resources["TextControlBorderBrushPointerOver"] = new SolidColorBrush(PaperEdge);
        Resources["TextControlBorderBrushFocused"]     = new SolidColorBrush(Color.FromRgb(0xC9, 0xB0, 0x3C));
        // Placeholder text defaults to a light muted grey (invisible on yellow) — make it dark ink.
        var inkMutedBrush = new SolidColorBrush(InkMuted);
        Resources["TextControlPlaceholderForeground"]            = inkMutedBrush;
        Resources["TextControlPlaceholderForegroundFocused"]     = inkMutedBrush;
        Resources["TextControlPlaceholderForegroundPointerOver"] = inkMutedBrush;

        // Buttons: Fluent's hover/pressed fills are light (unreadable under dark ink). Deepen them and pin
        // the text to ink so Save/Cancel/× stay legible in every state.
        var inkBrush = new SolidColorBrush(Ink);
        Resources["ButtonBackground"]            = Brushes.Transparent;
        Resources["ButtonBackgroundPointerOver"] = new SolidColorBrush(Color.FromRgb(0xD6, 0xBD, 0x45)); // deep amber
        Resources["ButtonBackgroundPressed"]     = new SolidColorBrush(Color.FromRgb(0xC2, 0xA9, 0x38)); // deeper still
        Resources["ButtonForeground"]            = inkBrush;
        Resources["ButtonForegroundPointerOver"] = inkBrush;
        Resources["ButtonForegroundPressed"]     = inkBrush;
        Resources["ButtonBorderBrush"]            = Brushes.Transparent;
        Resources["ButtonBorderBrushPointerOver"] = Brushes.Transparent;
        Resources["ButtonBorderBrushPressed"]     = Brushes.Transparent;

        var boxes = new List<TextBox>(sections.Count);
        _initial = new string[sections.Count];

        // The editable middle: one card per section, splitting the height evenly when there are two.
        var body = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        for (int i = 0; i < sections.Count; i++)
        {
            body.RowDefinitions.Add(new RowDefinition(GridLength.Star));
            var card = BuildSectionCard(sections[i], out var box);
            boxes.Add(box);
            _initial[i] = box.Text ?? "";
            card.Margin = new Thickness(0, i == 0 ? 0 : 8, 0, 0);
            Grid.SetRow(card, i);
            body.Children.Add(card);
        }
        _boxes = boxes;

        // Header: draggable title strip + close glyph.
        var titleText = new TextBlock
        {
            Text = title, FontFamily = HandFont, FontSize = 15, FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Ink), VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var close = new Button
        {
            Content = "✕", FontSize = 13, Width = 24, Height = 24, Padding = new Thickness(0),
            Background = Brushes.Transparent, Foreground = new SolidColorBrush(InkMuted),
            BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(12),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        close.Click += (_, _) => Close();
        var header = new Grid { Children = { titleText, close } };

        // Footer: Save / Cancel with a keyboard hint.
        var save = StickyButton("Save", Ink, strong: true);
        save.Click += (_, _) => SaveAndClose();
        var cancel = StickyButton("Cancel", InkMuted, strong: false);
        cancel.Click += (_, _) => Close();
        var hint = new TextBlock
        {
            Text = "Ctrl+Enter saves", FontFamily = HandFont, FontSize = 11,
            Foreground = new SolidColorBrush(InkMuted), VerticalAlignment = VerticalAlignment.Center,
        };
        var buttonStack = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        buttonStack.Children.Add(save);
        buttonStack.Children.Add(cancel);
        var footer = new Grid { Margin = new Thickness(0, 10, 0, 0), Children = { hint, buttonStack } };

        // Compose the paper: header pinned top, footer bottom, sections fill.
        var inner = new DockPanel { Margin = new Thickness(14, 10, 14, 12) };
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        inner.Children.Add(header);
        inner.Children.Add(footer);
        inner.Children.Add(body);

        var paper = new Border
        {
            Background = new SolidColorBrush(Paper),
            BorderBrush = new SolidColorBrush(PaperEdge), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3, 3, 14, 3),  // one softened "peeled" corner for sticky charm
            Margin = new Thickness(ShadowRoom),
            BoxShadow = BoxShadows.Parse("0 6 20 0 #55000000"),
            Child = inner,
        };
        // Drag the note from anywhere on the paper that isn't an input or a button, so it moves like a
        // real sticky rather than only from a title bar.
        paper.PointerPressed += (_, e) =>
        {
            if (e.Source is Control c && (c.FindAncestorOfType<TextBox>() is not null
                                          || c.FindAncestorOfType<Button>() is not null))
                return;
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
        };

        // A little strip of "tape" across the top, tilted for effect.
        var tape = new Border
        {
            Width = 84, Height = 20, HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, ShadowRoom - 8, 0, 0),
            Background = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            RenderTransform = new RotateTransform(-4),
            IsHitTestVisible = false, // decorative — don't swallow drags starting on the tape
        };

        Content = new Grid { Children = { paper, tape, BuildResizeGrip() } };

        // A multi-line TextBox consumes Enter (and Esc) for itself, so a bubbling OnKeyDown never sees the
        // Ctrl+Enter save. Intercept on the tunnel — before the focused box handles it — so the shortcuts
        // work while typing.
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
    }

    // A corner grip that drives the OS resize loop — a borderless window has no system resize border, so
    // this is how the sticky note is made resizable. Sits over the "peeled" bottom-right corner.
    private Control BuildResizeGrip()
    {
        var grip = new Border
        {
            Width = 22, Height = 22,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, ShadowRoom, ShadowRoom),
            Background = Brushes.Transparent,           // an invisible-but-hit-testable grab area
            Cursor = new Cursor(StandardCursorType.BottomRightCorner),
            Child = new global::Avalonia.Controls.Shapes.Path
            {
                Data = Geometry.Parse("M7,20 L20,7 M11,20 L20,11 M15,20 L20,15"),
                Stroke = new SolidColorBrush(InkMuted), StrokeThickness = 1.5,
                IsHitTestVisible = false,
            },
        };
        grip.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            BeginResizeDrag(WindowEdge.SouthEast, e);
            e.Handled = true; // don't let the press bubble into a move-drag
        };
        return grip;
    }

    // Builds one section card: a tinted accent tab + heading over a monospace editor on the bare paper.
    private TextBox BuildSectionCardBox(Section section) => new()
    {
        Text = section.Initial ?? "",
        PlaceholderText = section.Placeholder,
        Background = Brushes.Transparent,
        Foreground = new SolidColorBrush(Ink),
        CaretBrush = new SolidColorBrush(Ink),
        SelectionBrush = new SolidColorBrush(Color.FromArgb(0x66, 0xC9, 0xB0, 0x3C)),
        BorderThickness = new Thickness(0),
        FontFamily = MonoFont, FontSize = 13,
        Padding = new Thickness(2, 3, 2, 3),
        AcceptsReturn = true, AcceptsTab = false, TextWrapping = TextWrapping.Wrap,
        VerticalContentAlignment = VerticalAlignment.Top,
        [ScrollViewer.VerticalScrollBarVisibilityProperty] = ScrollBarVisibility.Auto,
    };

    private Border BuildSectionCard(Section section, out TextBox box)
    {
        box = BuildSectionCardBox(section);

        var content = new DockPanel();

        if (!string.IsNullOrEmpty(section.Heading))
        {
            var tab = new Rectangle
            {
                Width = 10, Height = 10, RadiusX = 2, RadiusY = 2,
                Fill = new SolidColorBrush(section.Accent),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0),
            };
            var heading = new TextBlock
            {
                Text = section.Heading, FontFamily = HandFont, FontSize = 13, FontWeight = FontWeight.Bold,
                Foreground = new SolidColorBrush(Ink), VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var headingRow = new StackPanel
            {
                Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4),
                Children = { tab, heading },
            };
            DockPanel.SetDock(headingRow, Dock.Top);
            content.Children.Add(headingRow);
        }

        content.Children.Add(box); // fills the rest of the card

        // Pure paper (no wash) so the note stays emphatically yellow; a faint accent underline is the only
        // thing separating stacked sections.
        return new Border
        {
            Background = Brushes.Transparent,
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, section.Accent.R, section.Accent.G, section.Accent.B)),
            BorderThickness = new Thickness(0, 0, 0, 2),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 4),
            Child = content,
        };
    }

    private static Button StickyButton(string text, Color ink, bool strong) => new()
    {
        Content = text, FontFamily = HandFont, FontSize = 13,
        Foreground = new SolidColorBrush(ink),
        // Opaque amber pills over the paper — Save a touch stronger than Cancel — with dark ink text, so
        // they stay readable at rest and the pointer-over deep-amber (see Resources) is an obvious change.
        Background = new SolidColorBrush(strong
            ? Color.FromRgb(0xEA, 0xD2, 0x5A)
            : Color.FromRgb(0xF3, 0xE1, 0x82)),
        BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(4),
        Padding = new Thickness(12, 5),
        HorizontalContentAlignment = HorizontalAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center,
    };

    private void SaveAndClose()
    {
        _saving = true;
        _persist();
        Close();
    }

    /// <summary>Closes the note without the unsaved-changes prompt — for programmatic teardown (app exit,
    /// the update flow via <c>CloseAuxWindows</c>) where popping a modal confirm would be inappropriate.</summary>
    public void CloseWithoutPrompt()
    {
        _closeConfirmed = true;
        Close();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        PlaceBesideOwner();
        if (_boxes.Count > 0)
        {
            _boxes[0].Focus();
            _boxes[0].CaretIndex = _boxes[0].Text?.Length ?? 0;
        }
    }

    // Tunnels from the window down to the focused control, so it fires before the multi-line editor
    // swallows Enter/Esc. Ctrl+Enter saves; Esc cancels (with the discard prompt if dirty).
    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            SaveAndClose();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel) return;
        // Saving, an already-confirmed discard, or an untouched note all close straight away.
        if (_saving || _closeConfirmed || !IsDirty()) return;
        e.Cancel = true;
        _ = ConfirmDiscardThenClose();
    }

    private bool IsDirty()
    {
        for (int i = 0; i < _boxes.Count; i++)
            if ((_boxes[i].Text ?? "") != _initial[i]) return true;
        return false;
    }

    private async Task ConfirmDiscardThenClose()
    {
        bool discard = await ConfirmDialog.ShowAsync(
            this, "Discard note changes?",
            "This note has unsaved changes. Close it without saving?",
            "Discard changes", "Keep editing");
        if (!discard) return;
        _closeConfirmed = true;
        Close();
    }

    // Position the note beside the overlay (its owner) on whichever side has the most room, so it never
    // lands on top of the always-on-top floating UI. Works in physical pixels, like the overlay's own
    // placement; leaves the manual default if the owner or its screen can't be resolved.
    private void PlaceBesideOwner()
    {
        if (Owner is not Window owner) return;
        var screen = owner.Screens.ScreenFromWindow(owner)
            ?? owner.Screens.Primary
            ?? (owner.Screens.All.Count > 0 ? owner.Screens.All[0] : null);
        if (screen is null) return;

        var wa = screen.WorkingArea;            // physical pixels
        double scale = screen.Scaling;

        var ownerRect = new PixelRect(owner.Position, PixelSize.FromSize(owner.Bounds.Size, scale));
        int dlgW = (int)(Width * scale);
        int dlgH = (int)(Height * scale);
        int gap = (int)(SideGap * scale);
        int cascade = (int)(CascadeIndex * CascadeStep * scale);

        int spaceLeft = ownerRect.X - wa.X;
        int spaceRight = (wa.X + wa.Width) - (ownerRect.Right);

        int x = spaceRight >= spaceLeft
            ? ownerRect.Right + gap        // more room to the right of the overlay
            : ownerRect.X - gap - dlgW;    // more room to the left
        x = Math.Clamp(x + cascade, wa.X, Math.Max(wa.X, wa.X + wa.Width - dlgW));

        // Align the note's top with the overlay's, then clamp so it stays fully on-screen.
        int y = Math.Clamp(ownerRect.Y + cascade, wa.Y, Math.Max(wa.Y, wa.Y + wa.Height - dlgH));

        Position = new PixelPoint(x, y);
    }
}

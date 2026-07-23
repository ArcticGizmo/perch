using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Perch.Avalonia.Theming;

namespace Perch.Avalonia.Windows;

/// <summary>
/// A tiny modal yes/no confirmation, dark-themed to match the rest of Perch. Returns <c>true</c> when the
/// user picks the affirmative (usually destructive) action, <c>false</c> otherwise — including Esc or
/// closing the window. Used for the sticky note's "discard unsaved changes?" prompt.
///
/// It is <b>topmost</b> and positions itself <em>clear of its owner's bounds</em>. The sticky note it
/// confirms for is owned by the always-on-top overlay (so it is effectively topmost); a plain centred
/// dialog sank behind it and couldn't be reached. Being topmost keeps it clickable; placing it beside the
/// note keeps it visible.
/// </summary>
internal sealed class ConfirmDialog : Window
{
    private const int Gap = 16; // DIP gap between the note and this dialog when placed beside it

    private ConfirmDialog(string title, string message, string confirmLabel, string cancelLabel)
    {
        Title = title;
        Width = 380;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual; // self-placed clear of the note in OnOpened
        Background = Palette.FormBgBrush;

        var heading = SettingsUi.SectionTitle(title);
        var body = SettingsUi.BodyText(message);

        var confirm = SettingsUi.FlatButton(confirmLabel);
        confirm.Width = 120;
        confirm.Click += (_, _) => Close(true);
        var cancel = SettingsUi.FlatButton(cancelLabel);
        cancel.Width = 120;
        cancel.Click += (_, _) => Close(false);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0),
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(confirm);

        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Children = { heading, body, buttons },
        };
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(false); e.Handled = true; }
        else if (e.Key == Key.Enter) { Close(true); e.Handled = true; }
        base.OnKeyDown(e);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        // SizeToContent settles Bounds on the next layout pass, so place once now (best-effort) and again
        // after the size is known, so the "clear of the note" maths uses the real dialog height.
        PlaceClearOfOwner();
        Dispatcher.UIThread.Post(PlaceClearOfOwner, DispatcherPriority.Loaded);
    }

    // Positions this dialog on whichever side of the owner note has the most room, fully outside the
    // note's bounds, vertically centred on it and clamped to the screen. Falls back to a screen-centre if
    // the owner or its screen can't be resolved.
    private void PlaceClearOfOwner()
    {
        if (Owner is not Window note) return;
        var screen = note.Screens.ScreenFromWindow(note)
            ?? note.Screens.Primary
            ?? (note.Screens.All.Count > 0 ? note.Screens.All[0] : null);
        if (screen is null) return;

        var wa = screen.WorkingArea;        // physical pixels
        double scale = screen.Scaling;

        var noteRect = new PixelRect(note.Position, PixelSize.FromSize(note.Bounds.Size, scale));
        int w = (int)((Bounds.Width > 0 ? Bounds.Width : Width) * scale);
        int h = (int)((Bounds.Height > 0 ? Bounds.Height : 160) * scale);
        int gap = (int)(Gap * scale);

        int spaceLeft = noteRect.X - wa.X;
        int spaceRight = (wa.X + wa.Width) - noteRect.Right;

        int x = spaceRight >= spaceLeft
            ? noteRect.Right + gap        // more room to the right of the note
            : noteRect.X - gap - w;       // more room to the left
        x = Math.Clamp(x, wa.X, Math.Max(wa.X, wa.X + wa.Width - w));

        int y = Math.Clamp(
            noteRect.Y + (noteRect.Height - h) / 2, wa.Y, Math.Max(wa.Y, wa.Y + wa.Height - h));

        Position = new PixelPoint(x, y);
    }

    /// <summary>Shows the confirmation modal owned by <paramref name="owner"/>; resolves to the user's
    /// choice. Placed beside (never over) the owner and kept topmost so it can't hide behind it.</summary>
    public static Task<bool> ShowAsync(
        Window owner, string title, string message, string confirmLabel, string cancelLabel) =>
        new ConfirmDialog(title, message, confirmLabel, cancelLabel).ShowDialog<bool>(owner);
}

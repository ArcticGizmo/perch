using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Perch.Avalonia.Theming;

namespace Perch.Avalonia.Windows;

/// <summary>
/// A small modal scratch-pad editor — free-form multi-line text, dark-themed to match the settings window.
/// Serves two callers: the <b>global</b> scratch pad (opened from the note button leading the quick-links
/// row) and a <b>per-session</b> note (opened from a session's right-click menu). On Save it closes with
/// <c>true</c> and exposes the entered text via <see cref="Text"/> (empty = clear). Kept focusable/modal
/// because the overlay is a no-activate tool window and can't take keystrokes itself.
/// </summary>
internal sealed class ScratchPadDialog : Window
{
    private readonly TextBox _textBox;

    /// <summary>The text the user entered, trimmed. Empty string means "no note / empty pad".</summary>
    public string Text => _textBox.Text?.Trim() ?? "";

    /// <param name="title">Window title — "Scratch pad" for the global pad, or the session name.</param>
    /// <param name="scopeHint">A one-line description shown above the editor (what this pad is attached to).</param>
    /// <param name="existingText">The current text to prefill, if any.</param>
    public ScratchPadDialog(string title, string scopeHint, string? existingText)
    {
        Title = title;
        Width = 460;
        Height = 360;
        MinWidth = 320;
        MinHeight = 220;
        CanResize = true;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Palette.FormBgBrush;

        var help = SettingsUi.BodyText(scopeHint);

        _textBox = SettingsUi.ThemedTextArea(existingText ?? "");
        _textBox.PlaceholderText = "Jot anything here — notes, todos, reminders…";

        var save = SettingsUi.FlatButton("Save");
        save.Width = 92;
        save.Click += (_, _) => Close(true);
        var cancel = SettingsUi.FlatButton("Cancel");
        cancel.Width = 92;
        cancel.Click += (_, _) => Close(false);

        var hint = new TextBlock
        {
            Text = "Ctrl+Enter to save · Esc to cancel",
            FontSize = 11, Foreground = Palette.MutedBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        buttons.Children.Add(save);
        buttons.Children.Add(cancel);
        var buttonRow = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        buttonRow.Children.Add(hint);
        buttonRow.Children.Add(buttons);

        // The editor fills the middle; help pinned above, buttons below.
        var layout = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(help, Dock.Top);
        DockPanel.SetDock(buttonRow, Dock.Bottom);
        help.Margin = new Thickness(0, 0, 0, 8);
        layout.Children.Add(help);
        layout.Children.Add(buttonRow);
        layout.Children.Add(_textBox); // last child fills the remaining space
        Content = layout;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _textBox.Focus();
        _textBox.CaretIndex = _textBox.Text?.Length ?? 0;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Enter inserts a newline (multi-line); Ctrl+Enter saves, Escape cancels.
        if (e.Key == Key.Escape) { Close(false); e.Handled = true; }
        else if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control)) { Close(true); e.Handled = true; }
        base.OnKeyDown(e);
    }
}

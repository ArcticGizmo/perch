using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Perch.Avalonia.Theming;

namespace Perch.Avalonia.Windows;

/// <summary>
/// A small modal dialog for adding or editing a session's pinned note — a short free-text label that
/// rides along in the overlay ("risky refactor", "waiting on review"). Dark-themed to match the settings
/// window and the <see cref="QuickLinkDialog"/> it's modelled on. On Save it closes with <c>true</c> and
/// exposes the entered text via <see cref="NoteText"/> (empty = clear the note). Kept focusable/modal
/// because the overlay is a no-activate tool window and can't take keystrokes itself.
/// </summary>
internal sealed class NoteDialog : Window
{
    private const int MaxLength = 140;

    private readonly TextBox _noteBox;
    private readonly TextBlock _count;

    /// <summary>The note the user entered, trimmed. Empty string means "no note" (clear it).</summary>
    public string NoteText => _noteBox.Text?.Trim() ?? "";

    public NoteDialog(string sessionName, string? existingNote)
    {
        Title = string.IsNullOrEmpty(existingNote) ? "Add note" : "Edit note";
        Width = 420;
        Height = 210;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Palette.FormBgBrush;

        var help = SettingsUi.BodyText(
            $"A short note pinned to “{sessionName}”. It shows in the overlay and survives a restart. " +
            "Leave it empty to clear the note.");

        _noteBox = SettingsUi.ThemedTextBox(existingNote ?? "");
        _noteBox.MaxLength = MaxLength;
        _noteBox.PlaceholderText = "e.g. risky refactor — waiting on review";

        _count = new TextBlock
        {
            FontSize = 11, Foreground = Palette.MutedBrush,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 4, 0, 0),
        };
        UpdateCount();
        _noteBox.TextChanged += (_, _) => UpdateCount();

        var save = SettingsUi.FlatButton("Save");
        save.Width = 92;
        save.Click += (_, _) => Close(true);
        var cancel = SettingsUi.FlatButton("Cancel");
        cancel.Width = 92;
        cancel.Click += (_, _) => Close(false);
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0),
        };
        buttons.Children.Add(save);
        buttons.Children.Add(cancel);

        var layout = new StackPanel { Margin = new Thickness(16) };
        layout.Children.Add(help);
        layout.Children.Add(SettingsUi.FieldCaption("Note"));
        layout.Children.Add(_noteBox);
        layout.Children.Add(_count);
        layout.Children.Add(buttons);
        Content = layout;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _noteBox.Focus();
        _noteBox.SelectAll();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(false); e.Handled = true; }
        else if (e.Key == Key.Enter) { Close(true); e.Handled = true; }
        base.OnKeyDown(e);
    }

    private void UpdateCount() => _count.Text = $"{_noteBox.Text?.Length ?? 0} / {MaxLength}";
}

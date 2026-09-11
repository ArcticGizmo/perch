using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Perch.Avalonia.Theming;
using Perch.Data;

namespace Perch.Avalonia.Windows;

/// <summary>
/// A small modal dialog for adding or editing a Claude Code config directory (config-dir discovery, Layer
/// 1): the directory itself (a Browse… folder picker, when adding) and its display <b>label</b> — the chip
/// shown on that dir's session rows. The label edit is transactional: nothing is written until Save, unlike
/// an inline field that commits on every keystroke. Dark-themed to match the settings window; on Save it
/// closes with <c>true</c> and exposes the chosen values via <see cref="DirPath"/> / <see cref="DirLabel"/>.
/// Modelled on <see cref="QuickLinkDialog"/>. Org-free: a label is a display string, nothing more.
/// </summary>
internal sealed class ConfigDirDialog : Window
{
    private readonly TextBox _pathBox;
    private readonly TextBox _labelBox;
    private readonly TextBlock _statusLabel;
    private readonly Button _ok;
    private readonly bool _editing;

    public string DirPath => _pathBox.Text?.Trim() ?? "";
    public string DirLabel => _labelBox.Text?.Trim() ?? "";

    /// <param name="existing">The dir being edited, or null to add a new one.</param>
    /// <param name="existingLabel">The dir's current custom label (edit mode), or null.</param>
    public ConfigDirDialog(ClaudeConfigDir? existing, string? existingLabel)
    {
        _editing = existing is not null;
        bool isPrimary = existing?.Provenance == ConfigDirProvenance.Primary;

        Title = _editing ? "Edit config directory" : "Add config directory";
        Width = 480;
        Height = 300;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Palette.FormBgBrush;

        var help = SettingsUi.BodyText(_editing
            ? "Rename how this directory appears on its session rows. The directory itself can't be moved from "
              + "here — remove it and add the new location instead."
            : "Point Perch at a Claude Code config directory (a CLAUDE_CONFIG_DIR root) that lives somewhere it "
              + "wouldn't auto-discover. Give it a label to control what shows on its session rows.");

        _pathBox = SettingsUi.ThemedTextBox(existing?.Root ?? "");
        Control pathControl;
        if (_editing)
        {
            _pathBox.IsReadOnly = true;
            _pathBox.Opacity = 0.7;
            pathControl = _pathBox;
        }
        else
        {
            var browse = SettingsUi.FlatButton("Browse…");
            browse.Margin = new Thickness(8, 0, 0, 0);
            browse.Click += async (_, _) => await BrowseAsync();
            var pathRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            Grid.SetColumn(_pathBox, 0);
            Grid.SetColumn(browse, 1);
            pathRow.Children.Add(_pathBox);
            pathRow.Children.Add(browse);
            pathControl = pathRow;
        }

        _statusLabel = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Palette.MutedBrush,
            MinHeight = 30, Margin = new Thickness(0, 5, 0, 0),
        };

        _labelBox = SettingsUi.ThemedTextBox(existingLabel ?? "");
        _labelBox.PlaceholderText = isPrimary
            ? "No label (default directory — no chip)"
            : $"Default: {existing?.Label ?? "the folder name"}";

        _ok = SettingsUi.FlatButton("Save");
        _ok.Width = 92;
        _ok.Click += (_, _) => Close(true);
        var cancel = SettingsUi.FlatButton("Cancel");
        cancel.Width = 92;
        cancel.Click += (_, _) => Close(false);
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0),
        };
        buttons.Children.Add(_ok);
        buttons.Children.Add(cancel);

        var layout = new StackPanel { Margin = new Thickness(16) };
        layout.Children.Add(help);
        layout.Children.Add(SettingsUi.FieldCaption("Directory"));
        layout.Children.Add(pathControl);
        layout.Children.Add(_statusLabel);
        layout.Children.Add(SettingsUi.FieldCaption("Label (optional)"));
        layout.Children.Add(_labelBox);
        Content = new ScrollViewer { Content = layout };

        _pathBox.TextChanged += (_, _) => RefreshStatus();
        RefreshStatus();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        (_editing ? _labelBox : _pathBox).Focus();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(false); e.Handled = true; }
        else if (e.Key == Key.Enter && _ok.IsEnabled) { Close(true); e.Handled = true; }
        base.OnKeyDown(e);
    }

    // Add mode needs a path before Save; the status line reflects whether it looks like a config dir yet.
    private void RefreshStatus()
    {
        if (!_editing) _ok.IsEnabled = DirPath.Length > 0;

        var path = DirPath;
        if (path.Length == 0) { SetStatus("", Palette.Muted); return; }
        if (ClaudeConfigDiscovery.LooksLikeConfigDir(path))
            SetStatus("✓  Looks like a Claude config directory.", Palette.Green);
        else
            SetStatus("⚠  Doesn't look like a Claude config directory yet (no sessions/, .claude.json, or "
                      + "settings.json + projects/).", Palette.Orange);
    }

    private void SetStatus(string text, Color color)
    {
        _statusLabel.Text = text;
        _statusLabel.Foreground = new SolidColorBrush(color);
    }

    private async System.Threading.Tasks.Task BrowseAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose a Claude config directory", AllowMultiple = false,
        });
        if (folders.Count == 0) return;
        var path = folders[0].TryGetLocalPath();
        if (!string.IsNullOrEmpty(path)) _pathBox.Text = path;
    }
}

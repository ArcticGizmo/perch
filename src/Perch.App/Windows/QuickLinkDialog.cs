using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Perch.Avalonia.Theming;
using Perch.Data;
using Perch.Platform;

namespace Perch.Avalonia.Windows;

/// <summary>
/// A small modal dialog for adding or editing a custom <see cref="QuickLink"/>: a display name and the
/// executable to launch (with a Browse… picker). Dark-themed to match the settings window. On Save it
/// closes with <c>true</c> and exposes the chosen values via <see cref="LinkName"/> / <see cref="LinkPath"/>.
/// The Avalonia port of the WinForms <c>QuickLinkDialog</c>; the name-resolution hint routes through the
/// platform <see cref="IAppIconProvider"/> seam rather than touching the shell directly.
/// </summary>
internal sealed class QuickLinkDialog : Window
{
    private readonly TextBox _nameBox;
    private readonly TextBox _pathBox;
    private readonly TextBlock _statusLabel;
    private readonly Button _ok;
    private readonly IAppIconProvider _icons;
    private readonly DispatcherTimer _nameCheck;

    public string LinkName => _nameBox.Text?.Trim() ?? "";
    public string LinkPath => _pathBox.Text?.Trim() ?? "";

    public QuickLinkDialog(QuickLink? existing, IAppIconProvider icons)
    {
        _icons = icons;

        Title = existing == null ? "Add quick link" : "Edit quick link";
        Width = 460;
        Height = 300;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Palette.FormBgBrush;

        var help = SettingsUi.BodyText(
            "Name the link after the app. If an installed app matches that name, its icon is found " +
            "automatically and a program path is optional — otherwise choose one below.");

        _nameBox = SettingsUi.ThemedTextBox(existing?.Name ?? "");
        _statusLabel = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Palette.MutedBrush,
            MinHeight = 34, Margin = new Thickness(0, 5, 0, 0),
        };

        _pathBox = SettingsUi.ThemedTextBox(existing?.ExePath ?? "");
        var browse = SettingsUi.FlatButton("Browse…");
        browse.Click += async (_, _) => await BrowseAsync();

        var pathRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(_pathBox, 0);
        browse.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(browse, 1);
        pathRow.Children.Add(_pathBox);
        pathRow.Children.Add(browse);

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
        layout.Children.Add(SettingsUi.FieldCaption("Name"));
        layout.Children.Add(_nameBox);
        layout.Children.Add(_statusLabel);
        layout.Children.Add(SettingsUi.FieldCaption("Program (optional)"));
        layout.Children.Add(pathRow);
        layout.Children.Add(buttons);
        Content = layout;

        _nameCheck = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _nameCheck.Tick += (_, _) => { _nameCheck.Stop(); RefreshNameStatus(); };

        _nameBox.TextChanged += (_, _) =>
        {
            _ok.IsEnabled = LinkName.Length > 0;
            _nameCheck.Stop();
            _nameCheck.Start();
        };
        _ok.IsEnabled = LinkName.Length > 0;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        RefreshNameStatus();
        _nameBox.Focus();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(false); e.Handled = true; }
        else if (e.Key == Key.Enter && _ok.IsEnabled) { Close(true); e.Handled = true; }
        base.OnKeyDown(e);
    }

    // Looks up whether the current name resolves to an app icon on its own (Start-Menu/Store match), via
    // the icon seam. The lookup can enumerate the Start Menu (~1s) so it runs off the UI thread.
    private void RefreshNameStatus()
    {
        string name = LinkName;
        if (name.Length == 0) { SetStatus("", Palette.Muted); return; }

        System.Threading.Tasks.Task.Run(() =>
        {
            bool found = _icons.GetIconFile(name, null, KnownApps.FindByName(name), 32) != null;
            Dispatcher.UIThread.Post(() =>
            {
                if (LinkName != name) return; // stale — the name changed while we looked
                if (found)
                    SetStatus($"✓  Found “{name}” — a program path is optional.", Palette.Green);
                else
                    SetStatus($"No installed app matches “{name}”. Set a program below to give it an icon.", Palette.Muted);
            });
        });
    }

    private void SetStatus(string text, Color color)
    {
        _statusLabel.Text = text;
        _statusLabel.Foreground = new SolidColorBrush(color);
    }

    private async System.Threading.Tasks.Task BrowseAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a program",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Programs") { Patterns = ["*.exe"] },
                new FilePickerFileType("All files") { Patterns = ["*"] },
            ],
        });

        if (files.Count == 0) return;
        var path = files[0].TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;

        _pathBox.Text = path;
        if (LinkName.Length == 0)
            _nameBox.Text = System.IO.Path.GetFileNameWithoutExtension(path);
    }
}

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
/// A small modal dialog for adding or editing a <see cref="QuickLink"/>: what it points at (an app, a file or
/// folder, or a web link), a display name, and the kind's target. Dark-themed to match the settings window. On
/// Save it closes with <c>true</c> and exposes the chosen values via <see cref="LinkKind"/>, <see cref="LinkName"/>,
/// <see cref="LinkPath"/> (an app's program) and <see cref="LinkTarget"/> (a file path or normalised URL). The
/// app name-resolution hint routes through the platform <see cref="IAppIconProvider"/> seam rather than touching
/// the shell directly.
/// </summary>
internal sealed class QuickLinkDialog : Window
{
    private static readonly string[] KindLabels = ["App", "File or folder", "Web link"];

    private readonly TextBox _nameBox;
    private readonly TextBox _pathBox;
    private readonly TextBox _fileBox;
    private readonly TextBox _urlBox;
    private readonly TextBlock _statusLabel;
    private readonly TextBlock _urlStatus;
    private readonly Control _convertHint;
    private readonly Control _appPanel;
    private readonly Control _filePanel;
    private readonly Control _urlPanel;
    private readonly ContentControl _kindHost = new();
    private readonly Button _ok;
    private readonly IAppIconProvider _icons;
    private readonly DispatcherTimer _nameCheck;
    private QuickLinkKind _kind;
    // Set once the user types a name, so picking a target stops proposing one.
    private bool _nameEdited;
    private bool _settingName;

    public QuickLinkKind LinkKind => _kind;
    public string LinkName => _nameBox.Text?.Trim() ?? "";
    public string LinkPath => _pathBox.Text?.Trim() ?? "";
    public string LinkTarget => _kind switch
    {
        QuickLinkKind.File => _fileBox.Text?.Trim() ?? "",
        QuickLinkKind.Url => QuickLinkRouting.NormalizeUrl(_urlBox.Text) ?? "",
        _ => "",
    };

    public QuickLinkDialog(QuickLink? existing, IAppIconProvider icons)
    {
        _icons = icons;
        _kind = existing?.Kind ?? QuickLinkKind.App;
        _nameEdited = !string.IsNullOrWhiteSpace(existing?.Name);

        Title = existing == null ? "Add quick link" : "Edit quick link";
        Width = 480;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Palette.FormBgBrush;

        _nameBox = SettingsUi.ThemedTextBox(existing?.Name ?? "");
        _statusLabel = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Palette.MutedBrush,
            MinHeight = 34, Margin = new Thickness(0, 5, 0, 0),
        };

        // App: the name finds an installed app; a program path is optional.
        _pathBox = SettingsUi.ThemedTextBox(existing?.ExePath ?? "");
        var browseExe = SettingsUi.FlatButton("Browse…");
        browseExe.Click += async (_, _) => await BrowseProgramAsync();
        _convertHint = BuildConvertHint();
        _appPanel = Stack(
            SettingsUi.BodyText("Name the link after the app. If an installed app matches that name, its icon is " +
                                "found automatically and a program path is optional. Otherwise choose one below."),
            _statusLabel,
            SettingsUi.FieldCaption("Program (optional)"),
            WithButtons(_pathBox, browseExe),
            _convertHint);

        // File or folder: opened with its default app.
        _fileBox = SettingsUi.ThemedTextBox(existing?.Kind == QuickLinkKind.File ? existing.Target : "");
        var browseFile = SettingsUi.FlatButton("File…");
        browseFile.Click += async (_, _) => await BrowseFileAsync();
        var browseFolder = SettingsUi.FlatButton("Folder…");
        browseFolder.Click += async (_, _) => await BrowseFolderAsync();
        _filePanel = Stack(
            SettingsUi.BodyText("Opens with its default app, and a folder opens in the file manager. A file link " +
                                "only opens files that app views; a program or script is shown in its folder " +
                                "instead, so add those as an App."),
            SettingsUi.FieldCaption("File or folder"),
            WithButtons(_fileBox, browseFile, browseFolder));

        // Web link: http(s) only.
        _urlBox = SettingsUi.ThemedTextBox(existing?.Kind == QuickLinkKind.Url ? existing.Target : "");
        _urlBox.PlaceholderText = "https://example.com";
        _urlStatus = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Palette.MutedBrush,
            Margin = new Thickness(0, 5, 0, 0),
        };
        _urlPanel = Stack(
            SettingsUi.BodyText("Opens in your browser. Middle-click it to open a new browser window. Its icon is " +
                                "the site's own, fetched once a week."),
            SettingsUi.FieldCaption("Address"),
            _urlBox,
            _urlStatus);

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

        _kindHost.Margin = new Thickness(0, 0, 0, 12);
        var layout = new StackPanel { Margin = new Thickness(16) };
        layout.Children.Add(_kindHost);
        layout.Children.Add(SettingsUi.FieldCaption("Name"));
        layout.Children.Add(_nameBox);
        layout.Children.Add(new Border { Height = 8 });
        layout.Children.Add(_appPanel);
        layout.Children.Add(_filePanel);
        layout.Children.Add(_urlPanel);
        layout.Children.Add(buttons);
        Content = layout;

        _nameCheck = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _nameCheck.Tick += (_, _) => { _nameCheck.Stop(); RefreshNameStatus(); };

        _nameBox.TextChanged += (_, _) =>
        {
            if (!_settingName) _nameEdited = LinkName.Length > 0;
            UpdateOk();
            _nameCheck.Stop();
            _nameCheck.Start();
        };
        _pathBox.TextChanged += (_, _) => UpdateConvertHint();
        _fileBox.TextChanged += (_, _) => UpdateOk();
        _urlBox.TextChanged += (_, _) => RefreshUrlStatus();

        SelectKind(_kind);
        RefreshUrlStatus();
    }

    private void RefreshUrlStatus()
    {
        var url = QuickLinkRouting.NormalizeUrl(_urlBox.Text);
        if (url is not null) ProposeName(new Uri(url).Host);
        _urlStatus.Text = url is null && !string.IsNullOrWhiteSpace(_urlBox.Text)
            ? "Enter a web address, such as https://example.com."
            : "";
        UpdateOk();
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

    private void SelectKind(QuickLinkKind kind)
    {
        _kind = kind;
        // Rebuilt rather than restyled so a programmatic switch (the convert hint) shows the right segment.
        _kindHost.Content = SettingsUi.Segmented(KindLabels, (int)kind, i => SelectKind((QuickLinkKind)i));
        _appPanel.IsVisible = kind == QuickLinkKind.App;
        _filePanel.IsVisible = kind == QuickLinkKind.File;
        _urlPanel.IsVisible = kind == QuickLinkKind.Url;
        UpdateConvertHint();
        UpdateOk();
    }

    private void UpdateOk()
    {
        _ok.IsEnabled = LinkName.Length > 0 && _kind switch
        {
            QuickLinkKind.File => LinkTarget.Length > 0,
            QuickLinkKind.Url => LinkTarget.Length > 0,
            _ => true,
        };
    }

    // An App link whose program path isn't a program (a picture someone wanted one click away) was the only way
    // to link a file before kinds existed. Offer the switch rather than making it silently.
    private Control BuildConvertHint()
    {
        var convert = SettingsUi.FlatButton("Make it a file link");
        convert.Click += (_, _) =>
        {
            _fileBox.Text = LinkPath;
            _pathBox.Text = "";
            SelectKind(QuickLinkKind.File);
        };
        var text = SettingsUi.BodyText("This path isn't a program.");
        text.VerticalAlignment = VerticalAlignment.Center;
        text.Margin = new Thickness(0, 0, 10, 0);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        row.Children.Add(text);
        row.Children.Add(convert);
        return row;
    }

    private void UpdateConvertHint()
    {
        var path = LinkPath;
        _convertHint.IsVisible = _kind == QuickLinkKind.App && path.Length > 0
            && !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            && Path.HasExtension(path);
    }

    // Looks up whether the current name resolves to an app icon on its own (Start-Menu/Store match), via
    // the icon seam. The lookup can enumerate the Start Menu (~1s) so it runs off the UI thread.
    private void RefreshNameStatus()
    {
        string name = LinkName;
        if (name.Length == 0 || _kind != QuickLinkKind.App) { SetStatus("", Palette.Muted); return; }

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

    private void ProposeName(string name)
    {
        if (_nameEdited || string.IsNullOrWhiteSpace(name)) return;
        _settingName = true;
        _nameBox.Text = name;
        _settingName = false;
    }

    private async System.Threading.Tasks.Task BrowseProgramAsync()
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
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { Length: > 0 } path) return;

        _pathBox.Text = path;
        ProposeName(Path.GetFileNameWithoutExtension(path));
    }

    private async System.Threading.Tasks.Task BrowseFileAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a file", AllowMultiple = false,
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { Length: > 0 } path) return;

        _fileBox.Text = path;
        ProposeName(Path.GetFileNameWithoutExtension(path));
    }

    private async System.Threading.Tasks.Task BrowseFolderAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose a folder", AllowMultiple = false,
        });
        if (folders.Count == 0 || folders[0].TryGetLocalPath() is not { Length: > 0 } path) return;

        _fileBox.Text = path;
        ProposeName(Path.GetFileName(Path.TrimEndingDirectorySeparator(path)));
    }

    private static StackPanel Stack(params Control[] children)
    {
        var panel = new StackPanel();
        foreach (var c in children) panel.Children.Add(c);
        return panel;
    }

    // A text box filling the row with its buttons to the right.
    private static Grid WithButtons(TextBox box, params Button[] buttons)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*" + string.Concat(buttons.Select(_ => ",Auto"))) };
        Grid.SetColumn(box, 0);
        grid.Children.Add(box);
        for (int i = 0; i < buttons.Length; i++)
        {
            buttons[i].Margin = new Thickness(8, 0, 0, 0);
            Grid.SetColumn(buttons[i], i + 1);
            grid.Children.Add(buttons[i]);
        }
        return grid;
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Perch.Avalonia.Theming;
using Perch.Data;
using Perch.Feeds;

namespace Perch.Avalonia.Windows;

/// <summary>
/// Add or edit one feed subscription (docs/feeds-plan.md §1, "Settings"). The address is checked before it's
/// saved: <b>Check</b> (or Save, when the current address hasn't been checked) fetches and parses it through the
/// same fenced <see cref="FeedFetcher"/> the engine uses, off the UI thread, and shows the feed's title, icon,
/// entry count and latest entry — or why it isn't a feed, with a next step (<see cref="FeedAddress.HintFor"/>).
/// A failed check turns Save into "Save anyway" (a feed can be down for a moment). Plain http is allowed with a
/// warning; a duplicate address is refused. Everything shown here came through the parser's cleaners.
/// </summary>
internal sealed class FeedDialog : Window
{
    private readonly FeedSubscription? _existing;
    private readonly IReadOnlyList<FeedSubscription> _others;
    private readonly FeedFetcher _fetcher = new();
    private readonly CancellationTokenSource _cts = new();

    private readonly TextBox _urlBox;
    private readonly TextBox _nameBox;
    private readonly CheckBox _images;
    private readonly TextBlock _urlNote;
    private readonly Button _check;
    private readonly Button _ok;
    private readonly Border _preview;
    private readonly Image _icon;
    private readonly TextBlock _previewTitle;
    private readonly TextBlock _previewDetail;
    private readonly TextBlock _previewHint;
    private readonly StackPanel _candidates = new() { Spacing = 6, Margin = new Thickness(0, 8, 0, 0), IsVisible = false };

    private string? _checkedUrl;   // the address the last check ran against
    private bool _checkOk, _checking;
    private bool _seeded;          // headless render: never reach the network

    /// <summary>The vetted address to save (after <c>true</c>).</summary>
    public Uri? Result { get; private set; }

    /// <summary>The user's name for the feed, or null to use the feed's own title.</summary>
    public string? TitleOverride => FeedText.Clean(_nameBox.Text, FeedText.FeedTitleMax) is { Length: > 0 } t ? t : null;

    /// <summary>Whether to load the images in this feed's posts.</summary>
    public bool ShowImages => _images.IsChecked == true;

    public FeedDialog(FeedSubscription? existing, IReadOnlyList<FeedSubscription> all)
    {
        _existing = existing;
        _others = all;

        Title = existing is null ? "Add feed" : "Edit feed";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Palette.FormBgBrush;

        _urlBox = SettingsUi.ThemedTextBox(existing?.Url ?? "");
        _urlBox.PlaceholderText = "https://example.com/feed";
        _check = SettingsUi.FlatButton("Check");
        _check.Margin = new Thickness(8, 0, 0, 0);
        _check.Click += async (_, _) => await CheckAsync();
        var urlRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(_check, 1);
        urlRow.Children.Add(_urlBox);
        urlRow.Children.Add(_check);

        _urlNote = new TextBlock
        {
            FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 0), IsVisible = false,
        };

        _icon = new Image { Width = 32, Height = 32, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Top };
        _previewTitle = new TextBlock { FontSize = 14, FontWeight = FontWeight.Bold, Foreground = Palette.TitleBrush, TextWrapping = TextWrapping.Wrap };
        _previewDetail = new TextBlock { FontSize = 12, Foreground = Palette.MutedBrush, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };
        _previewHint = new TextBlock { FontSize = 12, Foreground = Palette.MutedBrush, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), IsVisible = false };
        var previewText = new StackPanel();
        previewText.Children.Add(_previewTitle);
        previewText.Children.Add(_previewDetail);
        previewText.Children.Add(_previewHint);
        previewText.Children.Add(_candidates);
        var previewRow = new DockPanel();
        DockPanel.SetDock(_icon, Dock.Left);
        previewRow.Children.Add(_icon);
        previewRow.Children.Add(previewText);
        _preview = new Border
        {
            Child = previewRow, Padding = new Thickness(12), Margin = new Thickness(0, 12, 0, 0),
            CornerRadius = new CornerRadius(6), Background = Palette.SurfaceSunkenBrush, IsVisible = false,
        };

        _nameBox = SettingsUi.ThemedTextBox(existing?.TitleOverride ?? "");
        _nameBox.PlaceholderText = "Use the feed's own title";

        _images = new CheckBox
        {
            Content = "Show images in posts", IsChecked = existing?.ShowImages == true, Foreground = Palette.FgBrush,
            Margin = new Thickness(0, 12, 0, 0),
        };
        var imagesNote = new TextBlock
        {
            Text = "Off, images show as links. On, Perch loads them when you read a post, so the sites hosting them " +
                   "can see when you read.",
            FontSize = 12, Foreground = Palette.MutedBrush, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(28, 0, 0, 0),
        };

        _ok = SettingsUi.FlatButton("Save");
        _ok.MinWidth = 92;
        _ok.Click += async (_, _) => await SaveAsync();
        var cancel = SettingsUi.FlatButton("Cancel");
        cancel.Width = 92;
        cancel.Click += (_, _) => Close(false);
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0),
        };
        buttons.Children.Add(_ok);
        buttons.Children.Add(cancel);

        var layout = new StackPanel { Margin = new Thickness(16) };
        layout.Children.Add(SettingsUi.BodyText(
            "Paste the address of an Atom or RSS feed, or of a site that has one. Perch checks it before saving. " +
            "Feeds you add start quiet — " +
            "only entries published after this light up."));
        layout.Children.Add(SettingsUi.FieldCaption("Feed address"));
        layout.Children.Add(urlRow);
        layout.Children.Add(_urlNote);
        layout.Children.Add(_preview);
        layout.Children.Add(SettingsUi.FieldCaption("Name (optional)"));
        layout.Children.Add(_nameBox);
        layout.Children.Add(_images);
        layout.Children.Add(imagesNote);
        layout.Children.Add(buttons);
        Content = layout;

        _urlBox.TextChanged += (_, _) => OnUrlChanged();
        OnUrlChanged();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _urlBox.Focus();
        if (_existing is not null && !_seeded) _ = CheckAsync();
    }

    protected override void OnClosed(EventArgs e)
    {
        _cts.Cancel();
        _fetcher.Dispose();
        base.OnClosed(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(false); e.Handled = true; }
        else if (e.Key == Key.Enter && _ok.IsEnabled) { _ = SaveAsync(); e.Handled = true; }
        base.OnKeyDown(e);
    }

    // Re-validates the address as typed: the http warning, a duplicate, or a malformed address. Any earlier check
    // no longer applies.
    private void OnUrlChanged()
    {
        var parsed = FeedAddress.Parse(_urlBox.Text);
        string? note = null;
        bool warn = false, valid = parsed.Url is not null;

        if (parsed.Problem is { } problem) { note = problem; warn = true; }
        else if (parsed.Url is { } url && FeedAddress.IsDuplicate(url, _others, _existing?.Id))
        {
            note = "You already follow this feed.";
            warn = true;
            valid = false;
        }
        else if (parsed.Insecure)
        {
            note = "⚠ This feed uses plain http, so its content could be altered on the way to you. Use https if the site offers it.";
            warn = true;
        }

        _urlNote.Text = note ?? "";
        _urlNote.IsVisible = note is not null;
        _urlNote.Foreground = warn ? new SolidColorBrush(Palette.Yellow) : Palette.MutedBrush;

        bool stale = parsed.Url?.AbsoluteUri != _checkedUrl;
        if (stale) _preview.IsVisible = false;
        _check.IsEnabled = valid && !_checking;
        _ok.IsEnabled = valid && !_checking;
        // "Save anyway" only offers itself for a valid address whose check just failed.
        _ok.Content = valid && !stale && !_checkOk ? "Save anyway" : "Save";
    }

    private async Task SaveAsync()
    {
        var parsed = FeedAddress.Parse(_urlBox.Text);
        if (parsed.Url is not { } url || !_ok.IsEnabled) return;

        if (_checkedUrl != url.AbsoluteUri)
        {
            await CheckAsync();
            if (!_checkOk) return;   // the button now reads "Save anyway"
        }
        Result = url;
        Close(true);
    }

    private async Task CheckAsync()
    {
        var parsed = FeedAddress.Parse(_urlBox.Text);
        if (parsed.Url is not { } url || _checking) return;

        _checking = true;
        OnUrlChanged();
        ShowPreview("Checking…", "", null, warn: false);
        _icon.Source = null;

        var ct = _cts.Token;
        FeedFetchResult result;
        Bitmap? icon = null;
        try
        {
            // Fetch + parse + icon all off the UI thread (the parse is synchronous).
            (result, icon) = await Task.Run(async () =>
            {
                var r = await _fetcher.FetchFeedAsync(url, null, null, DateTime.UtcNow, ct);
                Bitmap? bmp = null;
                if (r is { Status: FeedFetchStatus.Ok, Doc: { } doc } && FeedsService.IconCandidate(doc, r.FinalUrl) is { } iconUrl)
                {
                    var bytes = await _fetcher.FetchIconAsync(iconUrl, r.IsPrivate ? url.IdnHost : null, ct);
                    if (bytes is not null && FeedIcon.Validate(bytes) is not null)
                    {
                        try { bmp = Bitmap.DecodeToWidth(new MemoryStream(bytes), 64); } catch { }
                    }
                }
                return (r, bmp);
            }, ct);
        }
        catch (OperationCanceledException)
        {
            return;   // the dialog closed
        }
        catch
        {
            result = new FeedFetchResult(FeedFetchStatus.Error, Error: "Couldn't fetch the feed");
        }

        if (!IsVisible) return;
        _checking = false;
        _checkedUrl = url.AbsoluteUri;
        _checkOk = result.Status == FeedFetchStatus.Ok;

        if (result.Doc is { } d)
        {
            _icon.Source = icon;
            _icon.IsVisible = icon is not null;
            var latest = d.Entries.FirstOrDefault();
            string count = d.Entries.Count == 1 ? "1 entry" : $"{d.Entries.Count} entries";
            string detail = latest is null ? count
                : $"{count}  ·  latest: {latest.Title} ({RelativeTime.Ago(DateTime.UtcNow, latest.Updated)})";
            ShowPreview("✓  " + d.Title, detail, null, warn: false);
            _nameBox.PlaceholderText = d.Title;
        }
        else if (result.Discovered is { Count: > 0 } found)
        {
            _icon.IsVisible = false;
            ShowDiscovered(found);
        }
        else
        {
            _icon.IsVisible = false;
            ShowPreview("Couldn't use this address", result.Error ?? "Unknown error", FeedAddress.HintFor(result.Error), warn: true);
        }
        OnUrlChanged();
    }

    // The address was a web page that advertises feeds: offer each one. Choosing one puts its address in the box and
    // checks it like any other — the page is untrusted, so nothing is followed without that check.
    private void ShowDiscovered(IReadOnlyList<FeedCandidate> found)
    {
        ShowPreview("This is a web page, not a feed",
            found.Count == 1 ? "It links to a feed:" : $"It links to {found.Count} feeds. Pick one:", null, warn: false);
        _candidates.Children.Clear();
        foreach (var c in found)
        {
            var name = new TextBlock
            {
                Text = (c.Title ?? FeedUrl.DisplayHost(c.Url)) + "  ·  " + c.Format, FontSize = 12.5,
                FontWeight = FontWeight.SemiBold, Foreground = Palette.TitleBrush, TextTrimming = TextTrimming.CharacterEllipsis,
            };
            // An address keeps its end legible (the path-trimming rule).
            var address = new TextBlock
            {
                Text = c.Url.AbsoluteUri, FontSize = 11.5, Foreground = Palette.MutedBrush,
                TextTrimming = TextTrimming.PrefixCharacterEllipsis,
            };
            var use = SettingsUi.FlatButton("Use this feed");
            use.VerticalAlignment = VerticalAlignment.Center;
            use.Margin = new Thickness(8, 0, 0, 0);
            var url = c.Url.AbsoluteUri;
            use.Click += async (_, _) =>
            {
                _urlBox.Text = url;   // OnUrlChanged hides this list (the old check no longer applies)
                await CheckAsync();
            };
            var row = new DockPanel();
            DockPanel.SetDock(use, Dock.Right);
            row.Children.Add(use);
            row.Children.Add(new StackPanel { Children = { name, address }, VerticalAlignment = VerticalAlignment.Center });
            _candidates.Children.Add(row);
        }
        _candidates.IsVisible = true;
    }

    private void ShowPreview(string title, string detail, string? hint, bool warn)
    {
        _preview.IsVisible = true;
        _previewTitle.Text = title;
        _previewTitle.Foreground = warn ? new SolidColorBrush(Palette.Yellow) : Palette.TitleBrush;
        _previewDetail.Text = detail;
        _previewDetail.IsVisible = detail.Length > 0;
        _previewHint.Text = hint ?? "";
        _previewHint.IsVisible = hint is not null;
        _candidates.IsVisible = false;
    }

    /// <summary>Headless-render seam: show a finished check without the network.</summary>
    internal void SeedForRender(FeedDoc? doc, string? error, IReadOnlyList<FeedCandidate>? discovered = null)
    {
        _seeded = true;
        var url = FeedAddress.Parse(_urlBox.Text).Url;
        _checkedUrl = doc is null && error is null ? null : url?.AbsoluteUri;   // neither: never checked
        _checkOk = doc is not null;
        if (discovered is { Count: > 0 })
        {
            _icon.IsVisible = false;
            ShowDiscovered(discovered);
        }
        else if (doc is null && error is null)
        {
            _preview.IsVisible = false;   // nothing checked: just the address validation
        }
        else if (doc is not null)
        {
            var latest = doc.Entries.FirstOrDefault();
            ShowPreview("✓  " + doc.Title,
                $"{doc.Entries.Count} entries  ·  latest: {latest?.Title} (2h ago)", null, warn: false);
            _icon.IsVisible = false;
            _nameBox.PlaceholderText = doc.Title;
        }
        else
        {
            _icon.IsVisible = false;
            ShowPreview("Couldn't use this address", error ?? "", FeedAddress.HintFor(error), warn: true);
        }
        OnUrlChanged();
    }
}

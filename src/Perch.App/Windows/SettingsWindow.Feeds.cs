using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Perch.Avalonia.Theming;
using Perch.Data;
using Perch.Feeds;

namespace Perch.Avalonia.Windows;

/// <summary>
/// The Feeds settings page (docs/feeds-plan.md §1, "Settings"): the subscription list with enable / reorder /
/// edit / remove, "Add feed…" (which checks the address before saving, in <see cref="FeedDialog"/>), and the
/// feed toggles and interval. The registry descriptors for the same settings power search and the catalogue;
/// this page is where the list itself is edited, like Quick Links.
/// </summary>
internal sealed partial class SettingsWindow
{
    private readonly List<FeedSubscription> _feeds = [];
    private StackPanel _feedsList = null!;

    private void BuildFeedsPage(StackPanel page)
    {
        page.Children.Add(SettingsUi.SectionTitle("Feeds"));
        page.Children.Add(SettingsUi.BodyText(
            "Follow Atom feeds — release notes, blogs, status pages. They show as a row of story-style heads on " +
            "the overlay: a head's ring lights up when its feed has something you haven't seen, and clicking it " +
            "plays the new entries. Feeds you add start quiet — only what's published after that lights up."));

        page.Children.Add(SettingsUi.TitleRow("Show the feeds row",
            SaveToggle(_settings.ShowFeeds, v =>
            {
                _settings.ShowFeeds = v;
                _hooks.FeedsChanged?.Invoke();
                _hooks.DisplayChanged?.Invoke();
            })));

        page.Children.Add(SettingsUi.Separator());

        _feeds.Clear();
        foreach (var f in _settings.Feeds ?? []) _feeds.Add(f.Clone());

        _feedsList = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        page.Children.Add(_feedsList);

        var addRow = SettingsUi.ButtonRow();
        var addBtn = SettingsUi.FlatButton("Add feed…");
        addBtn.Click += async (_, _) => await AddOrEditFeed(null);
        addRow.Children.Add(addBtn);
        page.Children.Add(addRow);

        page.Children.Add(SettingsUi.Separator());

        page.Children.Add(SettingsUi.TitleRow("Check every", BuildFeedsIntervalStepper()));
        page.Children.Add(SettingsUi.BodyText("Feeds are also checked shortly after Perch starts."));
        page.Children.Add(SettingsUi.TitleRow("Notify on new entries",
            SaveToggle(_settings.NotifyOnFeedEntry, v => _settings.NotifyOnFeedEntry = v)));

        page.Children.Add(SettingsUi.Separator());
        page.Children.Add(SettingsUi.BodyText(
            "Feed content is shown as plain formatted text: scripts, embedded media and remote images are never " +
            "loaded, and links open in your browser."));

        RebuildFeedsList();
    }

    /// <summary>Re-reads the engine's status for every row (the page was shown, or a check finished).</summary>
    public void RefreshFeedStatus()
    {
        if (_feedsList is not null) RebuildFeedsList();
    }

    private void RebuildFeedsList()
    {
        _feedsList.Children.Clear();
        if (_feeds.Count == 0)
        {
            _feedsList.Children.Add(new TextBlock
            {
                Text = "No feeds yet — add one below.", Foreground = Palette.MutedBrush,
                Margin = new Thickness(0, 4, 0, 4),
            });
            return;
        }
        for (int i = 0; i < _feeds.Count; i++) _feedsList.Children.Add(BuildFeedRow(_feeds[i], i));
    }

    private Control BuildFeedRow(FeedSubscription feed, int index)
    {
        var status = _hooks.FeedStatus?.Invoke(feed.Id);
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto,Auto,Auto"),
            Margin = new Thickness(0, 0, 0, 8),
        };

        var toggle = Toggle(feed.Enabled);
        toggle.VerticalAlignment = VerticalAlignment.Center;
        toggle.CheckedChanged += (_, _) => { feed.Enabled = toggle.IsChecked; RaiseFeedsChanged(); };
        Grid.SetColumn(toggle, 0);
        grid.Children.Add(toggle);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 8, 0) };
        text.Children.Add(new TextBlock
        {
            Text = FeedRowTitle(feed, status), FontSize = 14, FontWeight = FontWeight.Bold,
            Foreground = Palette.TitleBrush, TextTrimming = TextTrimming.CharacterEllipsis,
        });
        // A URL keeps its end legible and gives up its start (the path-trimming rule in CLAUDE.md).
        text.Children.Add(new TextBlock
        {
            Text = DisplayUrl(feed.Url), FontSize = 12, Foreground = Palette.MutedBrush,
            TextTrimming = TextTrimming.PrefixCharacterEllipsis,
        });
        var (line, warn) = FeedStatusLine(feed, status);
        text.Children.Add(new TextBlock
        {
            Text = line, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = warn ? new SolidColorBrush(Palette.Yellow) : Palette.MutedBrush,
        });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var up = SmallButton("↑", index > 0, () => MoveFeed(index, -1));
        Grid.SetColumn(up, 2);
        grid.Children.Add(up);
        var down = SmallButton("↓", index < _feeds.Count - 1, () => MoveFeed(index, +1));
        Grid.SetColumn(down, 3);
        grid.Children.Add(down);

        var edit = SettingsUi.FlatButton("Edit");
        edit.VerticalAlignment = VerticalAlignment.Center;
        edit.Margin = new Thickness(6, 0, 0, 0);
        edit.Click += async (_, _) => await AddOrEditFeed(feed);
        Grid.SetColumn(edit, 4);
        grid.Children.Add(edit);

        var remove = SettingsUi.FlatButton("Remove");
        remove.Foreground = new SolidColorBrush(Palette.Danger);
        remove.Margin = new Thickness(6, 0, 0, 0);
        remove.VerticalAlignment = VerticalAlignment.Center;
        remove.Click += (_, _) => { _feeds.Remove(feed); RebuildFeedsList(); RaiseFeedsChanged(); };
        Grid.SetColumn(remove, 5);
        grid.Children.Add(remove);

        return grid;
    }

    private static Button SmallButton(string glyph, bool enabled, Action click)
    {
        var b = SettingsUi.FlatButton(glyph);
        b.Width = 30;
        b.Margin = new Thickness(2, 0, 0, 0);
        b.VerticalAlignment = VerticalAlignment.Center;
        b.IsEnabled = enabled;
        b.Click += (_, _) => click();
        return b;
    }

    private static string FeedRowTitle(FeedSubscription feed, FeedHead? status)
    {
        var over = FeedText.Clean(feed.TitleOverride, FeedText.FeedTitleMax);
        if (over.Length > 0) return over;
        if (status?.Title is { Length: > 0 } t) return t;
        return Uri.TryCreate(feed.Url, UriKind.Absolute, out var u) ? FeedUrl.DisplayHost(u) : "Feed";
    }

    private static string DisplayUrl(string url) =>
        FeedUrl.Safe(url, null) is { } u ? u.AbsoluteUri : FeedText.Clean(url, 300);

    // "Latest: … · 2h ago", the last error, or why there's nothing yet. Plain http is called out either way.
    private (string Text, bool Warn) FeedStatusLine(FeedSubscription feed, FeedHead? status)
    {
        string insecure = feed.Url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? "  ·  ⚠ not secure (http)" : "";
        if (!feed.Enabled) return ("Paused" + insecure, insecure.Length > 0);
        if (!_settings.ShowFeeds) return ("Feeds are off — turn on \"Show the feeds row\" to check" + insecure, insecure.Length > 0);
        if (status is null || (!status.HasFetched && status.Error is null)) return ("Not checked yet" + insecure, insecure.Length > 0);
        if (status.Error is { } err) return ("⚠ " + err + insecure, true);
        var now = DateTime.UtcNow;
        string latest = status.LatestTitle is { } lt
            ? $"Latest: {lt}" + (status.LatestUtc is { } at ? $" · {RelativeTime.Ago(now, at)}" : "")
            : "No entries yet";
        string unread = status.UnreadCount > 0 ? $"{status.UnreadCount} new  ·  " : "";
        return (unread + latest + insecure, insecure.Length > 0);
    }

    private void MoveFeed(int index, int delta)
    {
        int to = index + delta;
        if (to < 0 || to >= _feeds.Count) return;
        (_feeds[index], _feeds[to]) = (_feeds[to], _feeds[index]);
        RebuildFeedsList();
        RaiseFeedsChanged();
    }

    private async System.Threading.Tasks.Task AddOrEditFeed(FeedSubscription? existing)
    {
        var dlg = new FeedDialog(existing, _feeds);
        bool ok = await dlg.ShowDialog<bool>(this);
        if (!ok || dlg.Result is not { } url) return;

        if (existing is null)
        {
            _feeds.Add(new FeedSubscription
            {
                Url = url.AbsoluteUri, TitleOverride = dlg.TitleOverride, Enabled = true, AddedUtc = DateTime.UtcNow,
            });
        }
        else
        {
            existing.Url = url.AbsoluteUri;   // same id: a changed URL re-primes in the engine
            existing.TitleOverride = dlg.TitleOverride;
        }
        RebuildFeedsList();
        RaiseFeedsChanged();
    }

    private void RaiseFeedsChanged()
    {
        _settings.Feeds = _feeds.Select(f => f.Clone()).ToList();
        _settings.Save();
        _hooks.FeedsChanged?.Invoke();
        _hooks.DisplayChanged?.Invoke();
    }

    // −/+ in 5-minute steps across the engine's 5–240 minute range.
    private Control BuildFeedsIntervalStepper()
    {
        const int min = 5, max = 240, step = 5;
        var row = SettingsUi.ButtonRow();
        var dec = SettingsUi.FlatButton("−");
        var inc = SettingsUi.FlatButton("+");
        dec.Width = 36; inc.Width = 36;
        var value = new TextBlock
        {
            Width = 70, TextAlignment = TextAlignment.Center, Foreground = Palette.FgBrush,
            VerticalAlignment = VerticalAlignment.Center, FontSize = 14,
        };
        void Render() => value.Text = $"{Math.Clamp(_settings.FeedsIntervalMinutes, min, max)} min";
        void Apply(int v)
        {
            v = Math.Clamp(v, min, max);
            if (v == _settings.FeedsIntervalMinutes) return;
            _settings.FeedsIntervalMinutes = v;
            _settings.Save();
            _hooks.FeedsChanged?.Invoke();
            Render();
        }
        dec.Click += (_, _) => Apply(Math.Clamp(_settings.FeedsIntervalMinutes, min, max) - step);
        inc.Click += (_, _) => Apply(Math.Clamp(_settings.FeedsIntervalMinutes, min, max) + step);
        Render();
        row.Children.Add(dec);
        row.Children.Add(value);
        row.Children.Add(inc);
        return row;
    }
}

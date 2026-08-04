using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Perch.Avalonia.Theming;
using Perch.Data;

namespace Perch.Avalonia.Windows;

/// <summary>
/// Raises the right <see cref="SettingsHooks"/> callback(s) to make a toggled setting take effect live.
/// Most settings only need the idempotent <see cref="SettingsHooks.DisplayChanged"/> re-apply; a handful
/// start/stop a poll or sampler and need their own hook too. This is the settings→hook map that the
/// dedicated pages each encode inline; centralising it lets search (and the M4 shell) drive any toggle at
/// full parity.
/// </summary>
internal static class SettingsLiveApply
{
    public static void Toggle(string id, SettingsHooks hooks, bool value)
    {
        switch (id)
        {
            case "usage-bars":            hooks.UsageEnabledChanged?.Invoke(value); break;
            case "service-status":        hooks.ServiceStatusEnabledChanged?.Invoke(value); break;
            case "media-controller":      hooks.MediaEnabledChanged?.Invoke(value); break;
            case "mic-presence":          hooks.MicEnabledChanged?.Invoke(value); break;
            case "hypertree":             hooks.HypertreeEnabledChanged?.Invoke(value); break;
            case "daemon-processes":      hooks.DaemonProcessesEnabledChanged?.Invoke(value); break;
            case "system-metrics":
            case "session-metrics":
            case "include-subprocess-metrics": hooks.MetricsChanged?.Invoke(); break;
            case "upside-down-quick-links":    hooks.QuickLinksChanged?.Invoke(); break;
        }

        // Always re-push the overlay display gates: cheap, idempotent, and covers the git/stuck/PR data-layer
        // flags too. Harmless for settings with no display effect (notifications, chimes, achievements).
        hooks.DisplayChanged?.Invoke();
    }
}

/// <summary>
/// The Settings search page: one field that filters <see cref="SettingsRegistry"/> across every setting by
/// name and keyword, so a setting can be found without knowing which page it lives on. Toggle results carry
/// a live <see cref="PerchToggle"/> wired straight to the real <see cref="AppSettings"/> (persisted and
/// applied through <see cref="SettingsLiveApply"/>); other kinds are shown find-only with a kind chip until
/// their editors move into the catalogue. A blank query lists everything — a built-in index of the app's
/// features.
/// </summary>
internal sealed class SettingsSearchView : StackPanel
{
    private readonly AppSettings _settings;
    private readonly SettingsHooks _hooks;
    private readonly TextBox _box;
    private readonly TextBlock _count;
    private readonly StackPanel _results;

    public SettingsSearchView(AppSettings settings, SettingsHooks hooks)
    {
        _settings = settings;
        _hooks = hooks;

        Children.Add(SettingsUi.SectionTitle("Search settings"));
        Children.Add(SettingsUi.BodyText(
            "Find any setting by name or keyword — try “chime”, “cost”, “git”, “cpu” or “phone”. " +
            "Leave it blank to browse every setting."));

        _box = SettingsUi.ThemedTextBox("");
        _box.PlaceholderText = "Search all settings…";
        _box.FontSize = 15;
        _box.Padding = new Thickness(10, 7);
        _box.Margin = new Thickness(0, 2, 0, 10);
        _box.TextChanged += (_, _) => Refresh();
        Children.Add(_box);

        _count = SettingsUi.FieldCaption("");
        Children.Add(_count);

        _results = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        Children.Add(_results);

        Refresh();
    }

    /// <summary>Focus the search field (posted so it works when the page is first shown).</summary>
    public void FocusSearch() => Dispatcher.UIThread.Post(() => _box.Focus(), DispatcherPriority.Input);

    /// <summary>Seed the query (drives a refresh) — used by the render harness to capture a filtered state.</summary>
    internal void SetQuery(string query) { _box.Text = query; Refresh(); }

    private void Refresh()
    {
        var query = _box.Text?.Trim() ?? "";
        _results.Children.Clear();

        var matches = SettingsRegistry.Search(query).ToList();
        _count.Text = matches.Count == 1 ? "1 setting" : $"{matches.Count} settings";

        if (matches.Count == 0)
        {
            _results.Children.Add(new TextBlock
            {
                Text = $"No setting matches “{query}”. Try a plainer word — “sound”, “ram”, “notify”.",
                Foreground = Palette.MutedBrush, FontSize = 13, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 14, 0, 0),
            });
            return;
        }

        foreach (var d in matches)
            _results.Children.Add(ResultRow(d, query));
    }

    private Control ResultRow(SettingDescriptor d, string query)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(0, 0, 0, 0),
        };

        var left = new StackPanel { Spacing = 2, Margin = new Thickness(0, 0, 12, 0) };
        left.Children.Add(new TextBlock
        {
            Text = d.Name, FontSize = 14, FontWeight = FontWeight.SemiBold, Foreground = Palette.TitleBrush,
        });

        var meta = $"{SettingDescriptor.SurfaceLabel(d.Surface)}  —  {d.Description}";
        if (MatchedKeywordOnly(d, query)) meta += "   ·  matched keyword";
        left.Children.Add(new TextBlock
        {
            Text = meta, FontSize = 12, Foreground = Palette.MutedBrush, TextWrapping = TextWrapping.Wrap,
        });
        Grid.SetColumn(left, 0);
        grid.Children.Add(left);

        Control right = d is { Kind: SettingKind.Toggle, GetBool: not null, SetBool: not null }
            ? LiveToggle(d)
            : KindChip(d.Kind);
        right.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);

        return new Border
        {
            Child = grid,
            Padding = new Thickness(2, 11, 2, 11),
            BorderBrush = Palette.BorderBrush,
            BorderThickness = new Thickness(0, 0, 0, 1),
        };
    }

    private PerchToggle LiveToggle(SettingDescriptor d)
    {
        var t = new PerchToggle();
        t.SetCheckedSilent(d.GetBool!(_settings));
        t.CheckedChanged += (_, _) =>
        {
            d.SetBool!(_settings, t.IsChecked);
            _settings.Save();
            SettingsLiveApply.Toggle(d.Id, _hooks, t.IsChecked);
        };
        return t;
    }

    // A muted pill naming the control a find-only result is edited with, so its home is legible even though
    // it isn't editable inline yet (slider / stepper / dropdown / hotkey / text field / list).
    private static Border KindChip(SettingKind kind)
    {
        var text = kind switch
        {
            SettingKind.Slider   => "slider",
            SettingKind.Stepper  => "stepper",
            SettingKind.Dropdown => "dropdown",
            SettingKind.Field    => "text",
            SettingKind.Hotkey   => "shortcut",
            SettingKind.List     => "list",
            _                    => kind.ToString().ToLowerInvariant(),
        };
        return new Border
        {
            Background = Palette.ButtonBgBrush, CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 3),
            Child = new TextBlock { Text = text, FontSize = 11, Foreground = Palette.MutedBrush },
        };
    }

    // True when the query matched only through a keyword/surface, not the visible name — so the row can note
    // why it surfaced (e.g. searching "sound" finding "Chime when done").
    private static bool MatchedKeywordOnly(SettingDescriptor d, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return false;
        var name = d.Name.ToLowerInvariant();
        foreach (var token in query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (!name.Contains(token, StringComparison.Ordinal))
                return true;
        return false;
    }
}

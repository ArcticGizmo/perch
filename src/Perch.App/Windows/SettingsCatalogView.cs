using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Perch.Avalonia.Theming;
using Perch.Data;

namespace Perch.Avalonia.Windows;

/// <summary>
/// The feature catalogue: every setting shown as a card, grouped by the surface it governs, so you can
/// browse what Perch can show you instead of hunting through pages. A filter box and a row of surface chips
/// narrow the board; toggles and steppers are editable inline (persisted + applied live at parity via
/// <see cref="SettingsLiveApply"/>), while richer kinds are shown find-only with a kind chip until their
/// bespoke editors move in. Each card carries a small representative preview of its glyph — the full live
/// preview is the docked <c>PreviewPane</c> (wired in M4).
/// </summary>
internal sealed class SettingsCatalogView : StackPanel
{
    private const double CardWidth = 320;

    private static readonly SettingSurface[] SurfaceOrder =
    [
        SettingSurface.SessionRow, SettingSurface.UsageBars, SettingSurface.SystemMetrics,
        SettingSurface.Notifications, SettingSurface.TrayAndStats, SettingSurface.Whimsy,
        SettingSurface.Integrations, SettingSurface.Advanced,
    ];

    private readonly AppSettings _settings;
    private readonly SettingsHooks _hooks;
    private readonly TextBox _filter;
    private readonly StackPanel _sections;
    private readonly List<(SettingSurface? surface, Button chip)> _chips = new();
    private SettingSurface? _active;   // null = all surfaces
    private ContextThresholdSliderView? _thresholdSlider;   // so "always show" can tint its green segment

    /// <summary>Raised after any inline edit persists, so a docked live preview can re-apply the settings.</summary>
    public event Action? Changed;

    /// <summary>Navigate to another settings page by key (for the few settings edited on a dedicated page).</summary>
    public Action<string>? Navigate;

    public SettingsCatalogView(AppSettings settings, SettingsHooks hooks)
    {
        _settings = settings;
        _hooks = hooks;

        Children.Add(SettingsUi.SectionTitle("Features"));
        Children.Add(SettingsUi.BodyText(
            "Every setting, grouped by where it appears on the overlay. Filter by name, or pick a surface."));

        _filter = SettingsUi.ThemedTextBox("");
        _filter.PlaceholderText = "Filter features…";
        _filter.Margin = new Thickness(0, 2, 0, 10);
        _filter.TextChanged += (_, _) => Rebuild();
        Children.Add(_filter);

        Children.Add(BuildChipRow());

        _sections = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        Children.Add(_sections);

        Rebuild();
    }

    private WrapPanel BuildChipRow()
    {
        var row = new WrapPanel { Margin = new Thickness(0, 0, 0, 2) };
        AddChip(row, null, "All surfaces");
        foreach (var s in SurfaceOrder)
            AddChip(row, s, SettingDescriptor.SurfaceLabel(s));
        RestyleChips();
        return row;
    }

    private void AddChip(WrapPanel row, SettingSurface? surface, string label)
    {
        var chip = new Button
        {
            Content = label, FontSize = 12, Margin = new Thickness(0, 0, 6, 6),
            Padding = new Thickness(11, 5), CornerRadius = new CornerRadius(999),
            BorderThickness = new Thickness(1), BorderBrush = Palette.BorderBrush,
        };
        chip.Click += (_, _) => { _active = surface; RestyleChips(); Rebuild(); };
        _chips.Add((surface, chip));
        row.Children.Add(chip);
    }

    private void RestyleChips()
    {
        foreach (var (surface, chip) in _chips)
        {
            bool on = Nullable.Equals(surface, _active);
            chip.Background = on ? Palette.AccentBrush : Palette.ButtonBgBrush;
            chip.Foreground = on ? Brushes.Black : Palette.MutedBrush;
            chip.BorderBrush = on ? Palette.AccentBrush : Palette.BorderBrush;
        }
    }

    private void Rebuild()
    {
        var query = _filter.Text?.Trim() ?? "";
        _sections.Children.Clear();

        int shown = 0;
        foreach (var surface in SurfaceOrder)
        {
            if (_active is { } a && a != surface) continue;

            var items = SettingsRegistry.All
                .Where(d => d.Surface == surface && d.Id != "context-green-segment" && d.MatchesQuery(query))
                .ToList();
            if (items.Count == 0) continue;

            shown += items.Count;
            _sections.Children.Add(SurfaceHeader(surface, items.Count));

            var cards = new WrapPanel();
            foreach (var d in items)
                cards.Children.Add(Card(d));
            _sections.Children.Add(cards);
        }

        if (shown == 0)
            _sections.Children.Add(new TextBlock
            {
                Text = $"No feature matches “{query}”.",
                Foreground = Palette.MutedBrush, FontSize = 13, Margin = new Thickness(2, 14, 0, 0),
            });
    }

    private static Control SurfaceHeader(SettingSurface surface, int count)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(2, 14, 0, 8),
        };
        row.Children.Add(new TextBlock
        {
            Text = SettingDescriptor.SurfaceLabel(surface).ToUpperInvariant(),
            FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = Palette.MutedBrush,
            VerticalAlignment = VerticalAlignment.Center,
        });
        row.Children.Add(new TextBlock
        {
            Text = count.ToString(), FontSize = 11, Foreground = Palette.MutedBrush,
            FontFamily = new FontFamily("Consolas, monospace"), VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.7,
        });
        return row;
    }

    private Border Card(SettingDescriptor d)
    {
        var stack = new StackPanel { Spacing = 8 };
        if (CardPreview(d.Preview) is { } preview) stack.Children.Add(preview);

        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var text = new StackPanel { Spacing = 2, Margin = new Thickness(0, 0, 10, 0) };
        text.Children.Add(new TextBlock
        {
            Text = d.Name, FontSize = 13.5, FontWeight = FontWeight.SemiBold, Foreground = Palette.TitleBrush,
            TextWrapping = TextWrapping.Wrap,
        });
        text.Children.Add(new TextBlock
        {
            Text = d.Description, FontSize = 12, Foreground = Palette.MutedBrush, TextWrapping = TextWrapping.Wrap,
        });
        Grid.SetColumn(text, 0);
        head.Children.Add(text);

        // Compact controls (toggle / stepper) sit to the right of the name; richer editors (dropdown, text
        // field, slider, hotkey, quick-links) render full-width below it so they have room to be used.
        var compact = CompactControl(d);
        if (compact is not null)
        {
            compact.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(compact, 1);
            head.Children.Add(compact);
        }
        stack.Children.Add(head);

        var editor = WideEditor(d);
        if (editor is not null)
        {
            editor.Margin = new Thickness(0, 4, 0, 0);
            stack.Children.Add(editor);
        }

        // The "green segment below the first threshold" option is a facet of context pressure, so it rides
        // on the same card as a small secondary toggle instead of getting a card of its own.
        if (d.Id == "context-pressure") stack.Children.Add(GreenSegmentRow());

        return new Border
        {
            Child = stack, Width = CardWidth, Margin = new Thickness(0, 0, 12, 12),
            Padding = new Thickness(14, 13), CornerRadius = new CornerRadius(10),
            Background = Palette.ButtonBgBrush, BorderThickness = new Thickness(1), BorderBrush = Palette.BorderBrush,
        };
    }

    private Control? CompactControl(SettingDescriptor d) => d switch
    {
        { Kind: SettingKind.Toggle, GetBool: not null, SetBool: not null }  => LiveToggle(d),
        { Kind: SettingKind.Stepper, GetInt: not null, SetInt: not null }    => LiveStepper(d),
        _                                                                     => null,
    };

    // The full-width editor for a non-compact setting, so no card is a dead end.
    private Control? WideEditor(SettingDescriptor d) => d.Kind switch
    {
        SettingKind.Dropdown => DropdownEditor(d),
        SettingKind.Field    => FieldEditor(d),
        SettingKind.Slider   => SliderEditor(),
        SettingKind.Hotkey   => HotkeyEditor(d),
        SettingKind.List     => ManageButton(d),
        _                    => null,
    };

    private Control DropdownEditor(SettingDescriptor d)
    {
        if (d.Id == "start-mode")
        {
            // Order matches the StartMode enum ordinals (Off, OnSessionStart, OnLogin).
            var combo = SettingsUi.Dropdown(["Never", "On session start", "At login"], (int)_settings.StartMode);
            combo.SelectionChanged += (_, _) =>
            {
                if (combo.SelectedIndex < 0) return;
                _settings.StartMode = (StartMode)combo.SelectedIndex;
                _settings.Save();
                App.SyncLoginItem(_settings.StartMode);   // "at login" also registers with the OS
            };
            return combo;
        }

        // reopen-terminal — order matches the TerminalApp enum ordinals.
        var term = SettingsUi.Dropdown(
            ["Auto", "Windows Terminal", "PowerShell", "Command Prompt"], (int)_settings.ReopenTerminal);
        term.SelectionChanged += (_, _) =>
        {
            if (term.SelectedIndex < 0) return;
            _settings.ReopenTerminal = (TerminalApp)term.SelectedIndex;
            _settings.Save();
        };
        return term;
    }

    private Control FieldEditor(SettingDescriptor d)
    {
        bool host = d.Id == "ntfy-host";
        var box = SettingsUi.ThemedTextBox((host ? _settings.NtfyHost : _settings.NtfyTopic) ?? "");
        box.PlaceholderText = host ? "https://ntfy.sh" : "your-topic-name";
        box.TextChanged += (_, _) =>
        {
            if (host) _settings.NtfyHost = box.Text;
            else _settings.NtfyTopic = box.Text;
            _settings.Save();
        };
        return box;
    }

    private Control SliderEditor()
    {
        var slider = new ContextThresholdSliderView { HorizontalAlignment = HorizontalAlignment.Stretch };
        slider.SetValues(_settings.ContextPressureYellowPercent,
            _settings.ContextPressureOrangePercent, _settings.ContextPressureRedPercent);
        slider.ShowGreenSegment = _settings.ShowContextGreenSegment;
        _thresholdSlider = slider;   // let the "always show context pressure" toggle repaint its green band
        slider.RangeChanged += (yellow, orange, red) =>
        {
            _settings.ContextPressureYellowPercent = yellow;
            _settings.ContextPressureOrangePercent = orange;
            _settings.ContextPressureRedPercent = red;
            _settings.Save();
            _hooks.DisplayChanged?.Invoke();   // re-push the thresholds onto the overlay + preview
            Changed?.Invoke();
        };
        return slider;
    }

    private Control HotkeyEditor(SettingDescriptor d)
    {
        var binding = d.Id switch
        {
            "hotkey-cycle"    => _settings.HotkeyCycleSessions,
            "hotkey-switcher" => _settings.HotkeyOpenSwitcher,
            _                 => _settings.HotkeyToggleDense,
        };
        var btn = new HotkeyCaptureButton(binding) { HorizontalAlignment = HorizontalAlignment.Left };
        btn.Changed += () => { _settings.Save(); _hooks.HotkeysChanged?.Invoke(); };
        return btn;
    }

    // Quick-links editing (add/remove/reorder/icon resolution) is its own surface; the card links to it.
    private Control ManageButton(SettingDescriptor _)
    {
        var btn = SettingsUi.FlatButton("Manage quick links  →");
        btn.HorizontalAlignment = HorizontalAlignment.Left;
        btn.Click += (_, _) => Navigate?.Invoke("quicklinks");
        return btn;
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
            Changed?.Invoke();
        };
        return t;
    }

    // The "always show context pressure" sub-toggle that rides on the context-pressure card (merged from
    // its own card). When on, the thresholds slider paints its below-first-threshold band bright green to
    // match what the overlay will draw.
    private Control GreenSegmentRow()
    {
        var t = new PerchToggle { VerticalAlignment = VerticalAlignment.Center };
        t.SetCheckedSilent(_settings.ShowContextGreenSegment);
        t.CheckedChanged += (_, _) =>
        {
            _settings.ShowContextGreenSegment = t.IsChecked;
            _settings.Save();
            SettingsLiveApply.Toggle("context-green-segment", _hooks, t.IsChecked);
            if (_thresholdSlider is not null) _thresholdSlider.ShowGreenSegment = t.IsChecked;
            Changed?.Invoke();
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var label = new TextBlock
        {
            Text = "Always show context pressure", FontSize = 12, Foreground = Palette.MutedBrush,
            TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        };
        Grid.SetColumn(label, 0);
        Grid.SetColumn(t, 1);
        grid.Children.Add(label);
        grid.Children.Add(t);

        return new Border
        {
            Child = grid, Margin = new Thickness(0, 6, 0, 0), Padding = new Thickness(0, 8, 0, 0),
            BorderThickness = new Thickness(0, 1, 0, 0), BorderBrush = Palette.BorderBrush,
        };
    }

    // A compact -/value/+ stepper bound to the descriptor's int. Clamped to a sane floor; the data layer
    // clamps again on apply, so an odd value can't misbehave.
    private Control LiveStepper(SettingDescriptor d)
    {
        var value = new TextBlock
        {
            Text = d.GetInt!(_settings).ToString(), FontSize = 13, Foreground = Palette.FgBrush,
            MinWidth = 26, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            FontFamily = new FontFamily("Consolas, monospace"),
        };

        void Bump(int delta)
        {
            var next = Math.Clamp(d.GetInt!(_settings) + delta, 1, 999);
            d.SetInt!(_settings, next);
            value.Text = next.ToString();
            _settings.Save();
            SettingsLiveApply.Value(d.Id, _hooks);
            Changed?.Invoke();
        }

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        row.Children.Add(StepButton("−", () => Bump(-1)));
        row.Children.Add(value);
        row.Children.Add(StepButton("+", () => Bump(+1)));
        return row;
    }

    private static Button StepButton(string glyph, Action onClick)
    {
        var b = new Button
        {
            Content = glyph, Width = 26, Height = 26, Padding = new Thickness(0),
            FontSize = 15, Background = Palette.FormBgBrush, Foreground = Palette.FgBrush,
            BorderThickness = new Thickness(1), BorderBrush = Palette.BorderBrush, CornerRadius = new CornerRadius(4),
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
        };
        b.Click += (_, _) => onClick();
        return b;
    }

    // A small representative glyph for the card — a taste of what the setting draws on the overlay. Static
    // on purpose; the docked live preview (M4) is where the real overlay reacts. Returns null for settings
    // with no single overlay glyph (behaviour/notification toggles), so those cards carry no preview chip.
    private static Control? CardPreview(PreviewTarget target)
    {
        var (text, fg, bg) = target switch
        {
            PreviewTarget.ModeBadge       => ("Plan",        Palette.Accent,  Color.FromRgb(38, 49, 74)),
            PreviewTarget.TaskProgress    => ("3 / 7",       Palette.Fg, Color.FromRgb(38, 38, 52)),
            PreviewTarget.ContextPressure => ("68%",         Palette.Orange,  Color.FromRgb(48, 40, 30)),
            PreviewTarget.WaitingTimer    => ("waiting 4m",  Palette.Yellow,  Color.FromRgb(45, 42, 28)),
            PreviewTarget.Artifacts       => ("◆ artifact", Color.FromRgb(196, 166, 255), Color.FromRgb(46, 38, 60)),
            PreviewTarget.BurnRate        => ("12.3k / m",   Palette.Fg, Color.FromRgb(38, 38, 52)),
            PreviewTarget.GitStats        => ("+142  −18", Palette.Green, Color.FromRgb(24, 40, 30)),
            PreviewTarget.PullRequest     => ("PR #1135",    Palette.Accent,  Color.FromRgb(38, 49, 74)),
            PreviewTarget.Note            => ("note",        Palette.Yellow,  Color.FromRgb(45, 42, 28)),
            PreviewTarget.UsageBars       => ("62%  weekly", Palette.Green,   Color.FromRgb(24, 40, 30)),
            PreviewTarget.ExpectedRate    => ("| on pace",   Palette.Muted, Color.FromRgb(38, 38, 52)),
            PreviewTarget.SystemMetrics   => ("CPU 12% · RAM 3.1G", Palette.Muted, Color.FromRgb(38, 38, 52)),
            PreviewTarget.SessionMetrics  => ("CPU · RAM",   Palette.Muted, Color.FromRgb(38, 38, 52)),
            PreviewTarget.MediaController => ("▷ track", Palette.Fg, Color.FromRgb(38, 38, 52)),
            PreviewTarget.MicPresence     => ("mic",         Color.FromRgb(94, 234, 212), Color.FromRgb(24, 42, 40)),
            PreviewTarget.DaemonProcesses => ("daemon",      Palette.Muted, Color.FromRgb(38, 38, 52)),
            PreviewTarget.QuickLinks      => ("links",       Palette.Muted, Color.FromRgb(38, 38, 52)),
            PreviewTarget.ServiceStatus   => ("status ok",   Palette.Green,   Color.FromRgb(24, 40, 30)),
            PreviewTarget.Stuck           => ("⚠ stuck", Palette.Orange, Color.FromRgb(48, 40, 30)),
            PreviewTarget.PerchReacts     => ("mood",        Palette.Brand,   Color.FromRgb(45, 40, 26)),
            _                             => ("",            Palette.Muted, Color.FromRgb(30, 30, 40)),
        };

        // No single glyph (notifications, behaviour toggles) — no preview chip at all, rather than an empty
        // box that reads as broken.
        if (text.Length == 0) return null;

        return new Border
        {
            Height = 28, CornerRadius = new CornerRadius(6), Padding = new Thickness(9, 0),
            Background = new SolidColorBrush(bg), HorizontalAlignment = HorizontalAlignment.Left,
            Child = new TextBlock
            {
                Text = text, FontSize = 11.5, Foreground = new SolidColorBrush(fg),
                FontFamily = new FontFamily("Consolas, monospace"), VerticalAlignment = VerticalAlignment.Center,
            },
        };
    }
}

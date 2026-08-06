using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Perch.Avalonia.Services;
using Perch.Avalonia.Theming;
using Perch.Avalonia.Views;
using Perch.Data;
using Perch.Theming;

namespace Perch.Avalonia.Windows;

/// <summary>
/// The theme designer — Perch's "accessibility is built in" showpiece. Starts from any built-in theme,
/// clones it into a draft applied live across the whole app as you edit (so the real overlay + windows
/// preview it), with a running WCAG contrast readout for every text/glyph pair and a one-click "Fix" that
/// nudges a failing colour until it passes. A saved theme lands in <see cref="AppSettings.CustomThemes"/>
/// and becomes selectable on the Appearance page.
///
/// <para>Editing is deliberately gentle: a single neutral-tint control (hue + strength) re-tints the whole
/// chrome ramp over the base's lightness structure — the "make my theme a bit more Perch-red" slider — plus
/// an HSL accent picker and a name. The semantic status hues are inherited unchanged, keeping the overlay
/// glanceable.</para>
/// </summary>
internal sealed class ThemeDesignerWindow : Window
{
    private readonly AppSettings _settings;
    private readonly Theme _restore;      // reverted to if the designer is cancelled/closed
    private readonly Action _onSaved;
    private readonly string? _editingId;  // non-null when editing an existing custom theme (replace, don't add)

    private Theme _base;                  // the starting theme (its lightness ramp + inherited roles)
    private double _hue, _chroma;
    private Rgb _accent;
    private string _name = "My Theme";
    private bool _saved;
    private bool _sync;                   // guards programmatic control updates from re-triggering handlers
    private CvdType _cvd = CvdType.None;  // colour-blind simulation applied to the live preview only
    private readonly List<(CvdType type, Button btn)> _cvdButtons = new();

    private Theme _draft;
    // Absolute colours pinned by a "Fix" (keyed by pair label); applied after the retint so the tint slider
    // can't overwrite a fix the user asked for.
    private readonly Dictionary<string, (Func<Theme, Rgb, Theme> Set, Rgb Value)> _overrides = new();

    private readonly StackPanel _readout = new() { Spacing = 6 };
    private readonly PreviewPane _preview = new();
    private Slider _hueSlider = null!, _chromaSlider = null!;
    private ColorPicker _accentPicker = null!;

    // The six pairs the readout audits: label, the draft role read as foreground (+ how to rewrite it when
    // "Fix" is pressed), the background role, and the target ratio (AA for text, 3:1 for glyphs).
    private readonly record struct Pair(
        string Label, Func<Theme, Rgb> Fg, Func<Theme, Rgb, Theme> WithFg, Func<Theme, Rgb> Bg,
        double Target, bool NonText);

    private static readonly Pair[] Pairs =
    [
        new("Body text on window",   t => t.TextPrimary, (t, c) => t with { TextPrimary = c }, t => t.Surface,        Contrast.AaText, false),
        new("Muted text on window",  t => t.TextMuted,   (t, c) => t with { TextMuted = c },   t => t.Surface,        Contrast.AaText, false),
        new("Body text on overlay",  t => t.TextPrimary, (t, c) => t with { TextPrimary = c }, t => t.OverlaySurface, Contrast.AaText, false),
        new("Muted text on overlay", t => t.TextMuted,   (t, c) => t with { TextMuted = c },   t => t.OverlaySurface, Contrast.AaText, false),
        new("Links / accent",        t => t.Accent,      (t, c) => t with { Accent = c },      t => t.Surface,        Contrast.NonText, true),
        new("Borders",               t => t.Border,      (t, c) => t with { Border = c },      t => t.Surface,        Contrast.NonText, true),
    ];

    /// <param name="seed">The theme the draft starts from — the active theme when creating, or the theme
    /// being edited when <paramref name="editingId"/> is set.</param>
    /// <param name="editingId">When non-null, the designer edits that existing custom theme: it opens seeded
    /// from it (name and all), and saving replaces the matching <see cref="AppSettings.CustomThemes"/> entry
    /// in place rather than appending a new one.</param>
    public ThemeDesignerWindow(AppSettings settings, Theme seed, Action onSaved, string? editingId = null)
    {
        _settings = settings;
        // Cancelling reverts to whatever was genuinely active before the designer opened — not the seed,
        // which for an edit may be a non-active theme.
        _restore = ThemeCatalog.Resolve(settings.ActiveThemeId, settings.CustomThemes);
        _onSaved = onSaved;
        _editingId = editingId;
        _base = seed;
        _draft = seed;
        _accent = seed.Accent;
        if (editingId is not null) _name = seed.Name;

        Title = editingId is null ? "Theme designer" : "Edit theme";
        Width = 900;
        Height = 780;
        MinWidth = 760;
        MinHeight = 560;
        Background = Palette.FormBgBrush;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = true;

        Content = BuildLayout();
        SeedFrom(seed);   // sets base, sliders, accent controls + draft, and applies live

        Closed += (_, _) => { if (!_saved) ThemeService.ApplyLive(_restore); };
    }

    private Control BuildLayout()
    {
        var root = new DockPanel { Margin = new Thickness(16) };

        // Bottom bar — always visible, so Save/Cancel never scroll off.
        var save = SettingsUi.FlatButton("Save theme");
        save.Background = Palette.AccentBrush;
        save.Foreground = Palette.OnAccentBrush;
        save.Click += (_, _) => Save();
        var cancel = SettingsUi.FlatButton("Cancel");
        cancel.Click += (_, _) => Close();
        var bottom = new Border
        {
            BorderThickness = new Thickness(0, 1, 0, 0), BorderBrush = Palette.BorderBrush,
            Padding = new Thickness(0, 10, 0, 0), Margin = new Thickness(0, 8, 0, 0),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right,
                Children = { save, cancel },
            },
        };
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);

        // Centre — controls on the left, live preview on the right.
        var center = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(BuildControls(out var controlsScroll), 0);
        center.Children.Add(controlsScroll);

        // The preview can be taller than the window, so it scrolls inside its own column — otherwise its
        // height would drive the whole layout and shove the docked footer (Save/Cancel) off-screen.
        var right = new DockPanel { Width = 280, Margin = new Thickness(16, 0, 0, 0) };
        var previewLabel = new TextBlock
        {
            Text = "LIVE PREVIEW", FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = Palette.MutedBrush,
            Margin = new Thickness(0, 0, 0, 10),
        };
        DockPanel.SetDock(previewLabel, Dock.Top);
        right.Children.Add(previewLabel);

        _preview.Apply(_settings);
        right.Children.Add(new ScrollViewer
        {
            Content = _preview,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        });
        Grid.SetColumn(right, 1);
        center.Children.Add(right);

        root.Children.Add(center);
        return root;
    }

    private Control BuildControls(out ScrollViewer scroll)
    {
        var left = new StackPanel { Spacing = 10, Margin = new Thickness(0, 0, 4, 0) };
        left.Children.Add(SettingsUi.SectionTitle("Design a theme"));
        left.Children.Add(SettingsUi.BodyText(
            "Start from a theme, re-tint the chrome and pick an accent. The status colours stay put so the " +
            "overlay stays readable. Every change is applied live and checked against WCAG contrast below."));

        left.Children.Add(SettingsUi.FieldCaption("Start from"));
        var baseNames = Themes.BuiltIn.Select(t => t.Name).ToList();
        var basePicker = SettingsUi.Dropdown(baseNames, Math.Max(0, Themes.BuiltIn.ToList().FindIndex(t => t.Id == _base.Id)));
        basePicker.SelectionChanged += (_, _) =>
        {
            if (_sync) return;
            var i = basePicker.SelectedIndex;
            if (i >= 0 && i < Themes.BuiltIn.Count) SeedFrom(Themes.BuiltIn[i]);
        };
        left.Children.Add(basePicker);

        left.Children.Add(SettingsUi.FieldCaption("Name"));
        var nameBox = SettingsUi.ThemedTextBox(_name);
        nameBox.TextChanged += (_, _) => _name = nameBox.Text ?? "My Theme";
        left.Children.Add(nameBox);

        left.Children.Add(SettingsUi.FieldCaption("Chrome tint — hue"));
        _hueSlider = MakeSlider(0, 360, _hue, v => { if (_sync) return; _hue = v; Recompute(); });
        left.Children.Add(_hueSlider);
        left.Children.Add(SettingsUi.FieldCaption("Chrome tint — strength"));
        _chromaSlider = MakeSlider(0, 0.30, _chroma, v => { if (_sync) return; _chroma = v; Recompute(); });
        left.Children.Add(_chromaSlider);

        left.Children.Add(SettingsUi.FieldCaption("Accent colour"));
        _accentPicker = new ColorPicker
        {
            Color = _accent.ToColor(), HorizontalAlignment = HorizontalAlignment.Left,
        };
        _accentPicker.ColorChanged += (_, e) =>
        {
            if (_sync) return;
            _accent = new Rgb(e.NewColor.R, e.NewColor.G, e.NewColor.B);
            Recompute();
        };
        left.Children.Add(_accentPicker);

        left.Children.Add(SettingsUi.Separator());
        left.Children.Add(SettingsUi.FieldCaption("Colour-blind preview"));
        left.Children.Add(BuildCvdRow());

        left.Children.Add(SettingsUi.Separator());
        left.Children.Add(new TextBlock
        {
            Text = "CONTRAST (WCAG)", FontSize = 11, FontWeight = FontWeight.SemiBold,
            Foreground = Palette.MutedBrush, Margin = new Thickness(0, 0, 0, 2),
        });
        left.Children.Add(_readout);

        scroll = new ScrollViewer
        {
            Content = left, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        return scroll;
    }

    // A row of buttons that run the live preview through a colour-vision-deficiency simulation, so a
    // designer can check the status hues stay distinguishable. Only the preview is simulated; the saved
    // theme and the contrast readout use the real colours.
    private Control BuildCvdRow()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        (CvdType type, string label)[] opts =
        [
            (CvdType.None, "Normal"), (CvdType.Protanopia, "Protan"),
            (CvdType.Deuteranopia, "Deutan"), (CvdType.Tritanopia, "Tritan"),
        ];
        foreach (var (type, label) in opts)
        {
            var btn = SettingsUi.FlatButton(label);
            btn.FontSize = 11;
            btn.Padding = new Thickness(8, 3);
            var t = type;
            btn.Click += (_, _) => { _cvd = t; RestyleCvd(); ApplyDraft(); };
            _cvdButtons.Add((type, btn));
            row.Children.Add(btn);
        }
        RestyleCvd();
        return row;
    }

    private void RestyleCvd()
    {
        foreach (var (type, btn) in _cvdButtons)
        {
            bool on = type == _cvd;
            btn.Background = on ? Palette.AccentBrush : Palette.ButtonBgBrush;
            btn.Foreground = on ? Palette.OnAccentBrush : Palette.FgBrush;
        }
    }

    // Reset every control to a starting theme, then apply.
    private void SeedFrom(Theme t)
    {
        _sync = true;
        _base = t;
        _overrides.Clear();
        var (h, s, _) = ColorMath.ToHsl(t.Surface);
        _hue = h;
        _chroma = s;
        _hueSlider.Value = h;
        _chromaSlider.Value = Math.Min(0.30, s);
        SetAccentControls(t.Accent);
        _sync = false;
        Recompute();
    }

    // A single accent edit (from the picker, a preset seed, or a "Fix"): sync the picker to it, recompute.
    private void OnAccentEdited(Rgb c)
    {
        _sync = true;
        SetAccentControls(c);
        _sync = false;
        Recompute();
    }

    // Push an accent colour into the picker (caller holds the _sync guard so it doesn't re-enter).
    private void SetAccentControls(Rgb c)
    {
        _accent = c;
        _accentPicker.Color = c.ToColor();
    }

    // Rebuild the draft from the tint/accent controls, apply it live, and refresh the readout.
    private void Recompute()
    {
        _draft = _base with
        {
            Name = _name,
            Surface            = ColorMath.Retint(_base.Surface, _hue, _chroma),
            SurfaceSunken      = ColorMath.Retint(_base.SurfaceSunken, _hue, _chroma),
            SurfaceRaised      = ColorMath.Retint(_base.SurfaceRaised, _hue, _chroma),
            SurfaceRaisedHover = ColorMath.Retint(_base.SurfaceRaisedHover, _hue, _chroma),
            OverlaySurface     = ColorMath.Retint(_base.OverlaySurface, _hue, _chroma),
            OverlayRowHover    = ColorMath.Retint(_base.OverlayRowHover, _hue, _chroma),
            Track              = ColorMath.Retint(_base.Track, _hue, _chroma),
            Border             = ColorMath.Retint(_base.Border, _hue, _chroma),
            Separator          = ColorMath.Retint(_base.Separator, _hue, _chroma),
            TreeLine           = ColorMath.Retint(_base.TreeLine, _hue, _chroma),
            // Text: a much fainter tint so it stays near-neutral but cohesive.
            TextPrimary  = ColorMath.Retint(_base.TextPrimary, _hue, _chroma * 0.25),
            TextTitle    = ColorMath.Retint(_base.TextTitle, _hue, _chroma * 0.25),
            TextMuted    = ColorMath.Retint(_base.TextMuted, _hue, _chroma * 0.25),
            ExpectedMark = ColorMath.Retint(_base.ExpectedMark, _hue, _chroma * 0.25),
            Accent       = _accent,
            AccentHover  = Lighten(_accent, 0.12),
        };

        foreach (var (set, value) in _overrides.Values)
            _draft = set(_draft, value);

        ApplyDraft();      // repaints the whole app + this window + the embedded preview
        RefreshReadout();  // ratios are always computed from the *real* draft, never the CVD simulation
    }

    // Apply the draft live, run through the current colour-blind simulation (None = the real draft).
    private void ApplyDraft() => ThemeService.ApplyLive(CvdSim.Simulate(_draft, _cvd));

    private void RefreshReadout()
    {
        _readout.Children.Clear();
        foreach (var p in Pairs)
            _readout.Children.Add(ReadoutRow(p));
    }

    private Control ReadoutRow(Pair p)
    {
        double ratio = Contrast.Ratio(p.Fg(_draft), p.Bg(_draft));
        bool passes = ratio >= p.Target;
        bool aaa = !p.NonText && ratio >= Contrast.AaaText;

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };

        var label = new TextBlock
        {
            Text = p.Label, FontSize = 12, Foreground = Palette.FgBrush, VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(label, 0);

        var (chipText, chipColor) = passes
            ? (aaa ? "AAA" : "AA", aaa ? Palette.Green : Palette.Yellow)
            : ("FAIL", Palette.Red);
        var chip = new Border
        {
            Background = new SolidColorBrush(chipColor), CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 1), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0),
            Child = new TextBlock
            {
                Text = $"{ratio:0.0}:1  {chipText}", FontSize = 11, FontWeight = FontWeight.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(20, 20, 26)),
            },
        };
        Grid.SetColumn(chip, 1);

        if (!passes)
        {
            var fix = SettingsUi.FlatButton("Fix");
            fix.FontSize = 11;
            fix.Padding = new Thickness(8, 1);
            fix.Margin = new Thickness(6, 0, 0, 0);
            fix.Click += (_, _) =>
            {
                var fixedFg = ColorMath.NudgeToContrast(p.Fg(_draft), p.Bg(_draft), p.Target);
                if (p.Label == "Links / accent")
                {
                    OnAccentEdited(fixedFg);
                }
                else
                {
                    _overrides[p.Label] = (p.WithFg, fixedFg);
                    Recompute();
                }
            };
            Grid.SetColumn(fix, 2);
            grid.Children.Add(fix);
        }

        grid.Children.Add(label);
        grid.Children.Add(chip);
        return grid;
    }

    private void Save()
    {
        var name = string.IsNullOrWhiteSpace(_name) ? "My Theme" : _name.Trim();
        var list = _settings.CustomThemes ??= new();

        Theme toSave;
        if (_editingId is { } editId)
        {
            toSave = _draft with { Id = editId, Name = name };
            var idx = list.FindIndex(t => t.Id == editId);
            if (idx >= 0) list[idx] = toSave; else list.Add(toSave);   // add if it vanished under us
            _settings.ActiveThemeId = editId;
        }
        else
        {
            var id = UniqueId(name);
            toSave = _draft with { Id = id, Name = name };
            list.Add(toSave);
            _settings.ActiveThemeId = id;
        }
        _settings.Save();

        _saved = true;
        ThemeService.ApplyLive(toSave);
        _onSaved();
        Close();
    }

    private string UniqueId(string name)
    {
        var slug = new string((name ?? "theme").ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray()).Trim('-');
        if (string.IsNullOrEmpty(slug)) slug = "theme";
        var baseId = "custom-" + slug;
        var taken = _settings.CustomThemes?.Select(t => t.Id).ToHashSet() ?? new();
        if (!taken.Contains(baseId) && !ThemeCatalog.IsBuiltIn(baseId)) return baseId;
        int n = 2;
        while (taken.Contains($"{baseId}-{n}")) n++;
        return $"{baseId}-{n}";
    }

    private static Rgb Lighten(Rgb c, double by)
    {
        var (h, s, l) = ColorMath.ToHsl(c);
        return ColorMath.FromHsl(h, s, Math.Min(1, l + by));
    }

    private static Slider MakeSlider(double min, double max, double value, Action<double> onChange)
    {
        var s = new Slider { Minimum = min, Maximum = max, Value = value, Foreground = Palette.AccentBrush };
        s.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty) onChange(s.Value); };
        return s;
    }
}

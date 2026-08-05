using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Perch.Avalonia.Services;
using Perch.Avalonia.Theming;
using Perch.Avalonia.Views;
using Perch.Data;
using Perch.Theming;

namespace Perch.Avalonia.Windows;

/// <summary>
/// The theme designer — Perch's "accessibility is built in" showpiece. Clones the active theme into an
/// editable draft, applied live across the whole app as you edit (so the real overlay + windows preview it),
/// with a running WCAG contrast readout for every text/glyph pair and a one-click "Fix" that nudges a
/// failing colour until it passes. A saved theme lands in <see cref="AppSettings.CustomThemes"/> and becomes
/// selectable on the Appearance page.
///
/// <para>Editing is deliberately gentle: a single neutral-tint control (hue + strength) re-tints the whole
/// chrome ramp at once — the "make my theme a bit more Perch-red" slider — over the base theme's lightness
/// structure, plus an accent picker and a name. The semantic status hues are inherited unchanged, keeping
/// the overlay glanceable.</para>
/// </summary>
internal sealed class ThemeDesignerWindow : Window
{
    private readonly AppSettings _settings;
    private readonly Theme _base;         // the seed's lightness structure + inherited (status/brand) roles
    private readonly Theme _restore;      // the theme to revert to if the designer is cancelled/closed
    private readonly Action _onSaved;

    private double _hue;
    private double _chroma;
    private Rgb _accent;
    private string _name;
    private bool _saved;

    private readonly StackPanel _readout = new() { Spacing = 6 };
    private readonly Border _accentSwatch = new() { Width = 26, Height = 26, CornerRadius = new CornerRadius(4) };
    private readonly TextBox _accentHex;

    // The six pairs the readout audits: label, the draft role it reads as foreground (+ how to rewrite it
    // when "Fix" is pressed), the background role, and the target ratio (AA for text, 3:1 for glyphs).
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

    private Theme _draft;
    private readonly PreviewPane _preview = new();

    // Absolute colours pinned by a "Fix" (keyed by pair label); applied after the retint so the tint slider
    // can't overwrite a fix the user asked for.
    private readonly Dictionary<string, (Func<Theme, Rgb, Theme> Set, Rgb Value)> _overrides = new();

    public ThemeDesignerWindow(AppSettings settings, Theme seed, Action onSaved)
    {
        _settings = settings;
        _base = seed;
        _restore = seed;
        _onSaved = onSaved;

        var (h, s, _) = ColorMath.ToHsl(seed.Surface);
        _hue = h;
        _chroma = s;
        _accent = seed.Accent;
        _name = "My Theme";
        _draft = seed;

        Title = "Theme designer";
        Width = 860;
        Height = 640;
        Background = Palette.FormBgBrush;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = true;

        _accentHex = SettingsUi.ThemedTextBox(_accent.ToHex());
        _accentHex.Width = 100;
        _accentHex.TextChanged += (_, _) =>
        {
            if (TryParseHex(_accentHex.Text, out var c)) { _accent = c; Recompute(); }
        };

        Content = BuildLayout();
        Recompute();

        Closed += (_, _) => { if (!_saved) ThemeService.ApplyLive(_restore); };
    }

    private Control BuildLayout()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(16) };

        // ── Left: editing controls + the WCAG readout ──
        var left = new StackPanel { Spacing = 12, Margin = new Thickness(0, 0, 16, 0) };
        left.Children.Add(SettingsUi.SectionTitle("Design a theme"));
        left.Children.Add(SettingsUi.BodyText(
            "Re-tint the chrome and pick an accent. The status colours stay put so the overlay stays " +
            "readable. Every change is applied live and checked against WCAG contrast below."));

        left.Children.Add(SettingsUi.FieldCaption("Name"));
        var nameBox = SettingsUi.ThemedTextBox(_name);
        nameBox.TextChanged += (_, _) => _name = nameBox.Text ?? "My Theme";
        left.Children.Add(nameBox);

        left.Children.Add(SettingsUi.FieldCaption("Chrome tint — hue"));
        left.Children.Add(MakeSlider(0, 360, _hue, v => { _hue = v; Recompute(); }));
        left.Children.Add(SettingsUi.FieldCaption("Chrome tint — strength"));
        left.Children.Add(MakeSlider(0, 0.30, _chroma, v => { _chroma = v; Recompute(); }));

        left.Children.Add(SettingsUi.FieldCaption("Accent colour"));
        var accentRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        accentRow.Children.Add(_accentSwatch);
        accentRow.Children.Add(_accentHex);
        left.Children.Add(accentRow);

        left.Children.Add(SettingsUi.Separator());
        left.Children.Add(new TextBlock
        {
            Text = "CONTRAST (WCAG)", FontSize = 11, FontWeight = FontWeight.SemiBold,
            Foreground = Palette.MutedBrush, Margin = new Thickness(0, 0, 0, 2),
        });
        left.Children.Add(_readout);

        var leftScroll = new ScrollViewer
        {
            Content = left, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        Grid.SetColumn(leftScroll, 0);

        // ── Right: live preview + save/cancel ──
        var right = new StackPanel { Spacing = 12, Width = 280 };
        right.Children.Add(new TextBlock
        {
            Text = "LIVE PREVIEW", FontSize = 11, FontWeight = FontWeight.SemiBold,
            Foreground = Palette.MutedBrush,
        });
        _preview.Apply(_settings);
        right.Children.Add(_preview);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 8, 0, 0) };
        var save = SettingsUi.FlatButton("Save theme");
        save.Background = Palette.AccentBrush;
        save.Click += (_, _) => Save();
        var cancel = SettingsUi.FlatButton("Cancel");
        cancel.Click += (_, _) => Close();
        buttons.Children.Add(save);
        buttons.Children.Add(cancel);
        right.Children.Add(buttons);
        Grid.SetColumn(right, 1);

        grid.Children.Add(leftScroll);
        grid.Children.Add(right);
        return grid;
    }

    // Rebuild the draft from the tint/accent controls, apply it live, and refresh the readout.
    private void Recompute()
    {
        _draft = _base with
        {
            Name = _name,
            // Neutral chrome: re-hued over the base's lightness ramp.
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
            AccentHover  = ColorMath.FromHsl(ColorMath.ToHsl(_accent).H, ColorMath.ToHsl(_accent).S,
                               Math.Min(1, ColorMath.ToHsl(_accent).L + 0.12)),
        };

        // Re-apply any pinned "Fix" colours over the retint.
        foreach (var (set, value) in _overrides.Values)
            _draft = set(_draft, value);

        _accentSwatch.Background = _accent.ToBrush();
        ThemeService.ApplyLive(_draft);   // repaints the whole app + this window + the embedded preview
        RefreshReadout();
    }

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
            Text = p.Label, FontSize = 12, Foreground = Palette.FgBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(label, 0);

        var (chipText, chipColor) = passes
            ? (aaa ? "AAA" : "AA", aaa ? Palette.Green : Palette.Yellow)
            : ("FAIL", Palette.Red);
        var chip = new Border
        {
            Background = new SolidColorBrush(chipColor), CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 1), VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
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
                    _accent = fixedFg;
                    _accentHex.Text = fixedFg.ToHex();   // TextChanged → Recompute
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
        var id = UniqueId(_name);
        var toSave = _draft with { Id = id, Name = string.IsNullOrWhiteSpace(_name) ? "My Theme" : _name.Trim() };

        _settings.CustomThemes ??= new();
        _settings.CustomThemes.Add(toSave);
        _settings.ActiveThemeId = id;
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

    private static bool TryParseHex(string? s, out Rgb c)
    {
        try { c = Rgb.FromHex(s ?? ""); return true; }
        catch { c = default; return false; }
    }

    private static Slider MakeSlider(double min, double max, double value, Action<double> onChange)
    {
        var s = new Slider { Minimum = min, Maximum = max, Value = value, Foreground = Palette.AccentBrush };
        s.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty) onChange(s.Value);
        };
        return s;
    }
}

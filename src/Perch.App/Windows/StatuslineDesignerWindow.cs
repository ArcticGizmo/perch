using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using Perch.Avalonia.Rendering;
using Perch.Avalonia.Theming;
using Perch.Data;
using Perch.Statusline;

namespace Perch.Avalonia.Windows;

/// <summary>
/// The graphical statusline designer (M2): edit a mustache template on the left/centre, watch it render
/// live against a sample payload, and click fields from the data explorer on the right to insert them.
/// The profile rail switches between Perch templates and imported (verbatim) commands; "Set active"
/// compiles the selected profile to the standalone Node script and points <c>settings.json</c> at it.
///
/// <para>Real Avalonia controls (a monospace <see cref="TextBox"/> for the template, coloured
/// <see cref="Run"/>s for the preview) rather than owner-drawing — this is a document-style tool window,
/// not an overlay surface. The live preview goes through the same <see cref="StatuslineTemplate"/> engine
/// the generated script mirrors, so what you see is what the terminal shows. Single reused instance via
/// <c>WindowHost.ShowOrFocus</c>; the profile library is saved on close.</para>
/// </summary>
internal sealed class StatuslineDesignerWindow : Window
{
    private static readonly IBrush Bg     = Palette.OverlaySurfaceBrush;
    private static readonly IBrush Panel  = Palette.ButtonBgBrush;
    private static readonly IBrush Stroke = Palette.BorderBrush;
    private static readonly IBrush Fg     = Palette.FgBrush;
    private static readonly IBrush Muted  = Palette.MutedBrush;
    private static readonly IBrush Accent = Palette.AccentBrush;

    // The terminal preview is painted in fixed near-terminal colours (not theme roles) so it reads like a
    // real status bar; token colours come straight from StatusColors so preview == ANSI output.
    private static readonly IBrush TermBg = new SolidColorBrush(Color.FromRgb(0x07, 0x09, 0x0d));
    private static readonly IBrush TermFg = new SolidColorBrush(Color.FromRgb(0xc9, 0xd3, 0xe0));
    private static readonly IBrush GutterBg = new SolidColorBrush(Color.FromRgb(0x0c, 0x10, 0x16));
    private static readonly FontFamily Mono = new("Cascadia Code,Consolas,Menlo,monospace");
    private const double EditorPadTop = 8;

    private readonly StatuslineConfig _config;
    private readonly TemplateData _sample = StatuslineSample.Data();
    private StatuslineProfile _selected;

    // control refs rebuilt/updated as selection and text change
    private StackPanel _railPerch = null!;
    private StackPanel _railExt = null!;
    private TextBox _nameBox = null!;
    private HighlightTextBox _templateBox = null!;
    private TextBox _commandBox = null!;
    private Panel _perchEditor = null!;
    private Panel _extEditor = null!;
    private StackPanel _preview = null!;
    private TextBlock _editorHeader = null!;
    private WrapPanel _chips = null!;
    private TextBlock _softBreakHint = null!;
    private Border _lockBanner = null!;
    private StackPanel _applyArea = null!;
    private List<ClaudeConfigDir> _applyDirs = new();
    private DispatcherTimer? _appliedTimer;

    // editor gutter + autocomplete
    private StackPanel _gutter = null!;
    private StackPanel _completionPanel = null!;
    private Border _completionHost = null!;
    private List<(string Path, string Value)> _completions = new();
    private int _completionIndex, _completionStart, _completionLen;
    private CompKind _completionKind;
    private bool _suppress;   // guards the completion refresh while we programmatically set editor text

    // What the caret is positioned to complete: a field path, a filter name, a color:name arg, or the
    // if/unless keyword right after a {{# sigil.
    private enum CompKind { Path, Filter, Color, Section }

    private static readonly (string Name, string Sig)[] FilterCatalog =
    {
        ("bar", "N-cell bar"), ("money", "0.00"), ("pct", "rounded %"), ("round", "0 dp · round:N"),
        ("k", "1.2k"), ("human", "68k / 2M"), ("dur", "1h 15m"), ("until", "countdown"),
        ("upper", "UPPERCASE"), ("lower", "lowercase"), ("trunc", "trunc:N"), ("default", "default:X"),
        ("color", "color:name"), ("pace", "pace:resets:secs"),
    };

    public StatuslineDesignerWindow() : this(StatuslineStore.Load()) { }

    /// <summary>Test/preview seam: build against a supplied config instead of the on-disk library, so
    /// <c>HeadlessRenderer</c> renders a deterministic set (and the seeded built-in examples) rather than
    /// whatever happens to be saved.</summary>
    internal StatuslineDesignerWindow(StatuslineConfig config)
    {
        _config = config;
        _selected = _config.Active ?? _config.Profiles[0];

        Title = $"Statusline designer{Perch.Data.AppProfile.DisplaySuffix}";
        Background = Bg;
        Width = 1000;
        Height = 640;
        MinWidth = 720;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        Content = BuildContent();
        SelectProfile(_selected);

        // The config-dir set can change while this reused single-instance window is open (a session self-
        // reports a dir, or the user declares one) — refresh the per-dir apply rows each time it's activated.
        Activated += (_, _) => RebuildApplyArea();
    }

    protected override void OnClosed(EventArgs e)
    {
        // Persist the profile library (edits, new/imported profiles) even if the user never hit "Set active".
        StatuslineStore.Save(_config);
        base.OnClosed(e);
    }

    // ── layout ──────────────────────────────────────────────────────────────────────────
    private Control BuildContent()
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("248,*,320"),
        };

        var left = Column(BuildLeft());
        Grid.SetColumn(left, 0);

        var centre = Column(BuildCentre());
        Grid.SetColumn(centre, 1);

        var right = Column(BuildRight());
        Grid.SetColumn(right, 2);

        grid.Children.Add(left);
        grid.Children.Add(centre);
        grid.Children.Add(right);
        return grid;
    }

    private static Border Column(Control child) => new()
    {
        BorderBrush = Stroke,
        BorderThickness = new Thickness(0, 0, 1, 0),
        Child = new ScrollViewer { Content = child, Padding = new Thickness(16) },
    };

    // ── left: profile rail ────────────────────────────────────────────────────────────────
    private Control BuildLeft()
    {
        _railPerch = new StackPanel { Spacing = 6 };
        _railExt = new StackPanel { Spacing = 6 };

        var newBtn = TextButton("＋  New template", () =>
        {
            var p = new StatuslineProfile
            {
                Name = UniqueName("New template"),
                Kind = ProfileKind.Perch,
                Template = "{{model.display_name}} · {{context_window.used_percentage|pct}}",
            };
            _config.Profiles.Add(p);
            RefreshRail();
            SelectProfile(p);
        });

        var backupBtn = TextButton("⤓  Back up current", () =>
        {
            var current = StatuslineInstaller.CurrentCommand();
            if (string.IsNullOrWhiteSpace(current)) { Flash(_editorHeader, "Nothing in settings.json to back up"); return; }
            var p = new StatuslineProfile { Name = UniqueName("settings.json backup"), Kind = ProfileKind.External, Command = current };
            _config.Profiles.Add(p);
            RefreshRail();
            SelectProfile(p);
        });

        var importBtn = TextButton("⇪  Import command…", () =>
        {
            var p = new StatuslineProfile { Name = UniqueName("Imported command"), Kind = ProfileKind.External, Command = "" };
            _config.Profiles.Add(p);
            RefreshRail();
            SelectProfile(p);
        });

        var stack = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                Eyebrow("Profiles"),
                _railPerch,
                DivLabel("Imported · non-Perch"),
                _railExt,
                new Border { Height = 6 },
                newBtn, backupBtn, importBtn,
            },
        };
        RefreshRail();
        return stack;
    }

    private void RefreshRail()
    {
        _railPerch.Children.Clear();
        _railExt.Children.Clear();
        foreach (var p in _config.Profiles)
        {
            var row = ProfileRow(p);
            (p.IsPerch ? _railPerch : _railExt).Children.Add(row);
        }
    }

    private Control ProfileRow(StatuslineProfile p)
    {
        bool selected = ReferenceEquals(p, _selected);
        bool active = ReferenceEquals(p, _config.Active);

        var nub = new Ellipse
        {
            Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center,
            Fill = active ? Accent : Muted,
        };
        var name = new TextBlock
        {
            Text = p.Name, Foreground = Fg, FontWeight = FontWeight.SemiBold, FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
        };
        var tag = new Border
        {
            Background = p.IsPerch ? Tint(Accent) : Panel,
            CornerRadius = new CornerRadius(4), Padding = new Thickness(5, 1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = p.IsPerch ? "perch" : "ext", FontSize = 9.5, FontFamily = Mono, Foreground = p.IsPerch ? Accent : Muted },
        };
        var dock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(nub, Dock.Left);
        DockPanel.SetDock(tag, Dock.Right);
        dock.Children.Add(nub);
        dock.Children.Add(tag);
        dock.Children.Add(new Border { Child = name, Margin = new Thickness(9, 0, 8, 0) });

        var border = new Border
        {
            Background = selected ? Tint(Accent) : Panel,
            BorderBrush = selected ? Accent : Stroke,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 8),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = dock,
        };
        border.PointerPressed += (_, e) =>
        {
            // Left-press selects; let a right-press fall through to the context menu without selecting.
            if (e.GetCurrentPoint(border).Properties.IsRightButtonPressed) return;
            SelectProfile(p);
        };

        // Right-click: duplicate (always) and, for user/imported profiles, delete. Built-ins can't be
        // deleted — they come from code and would just reappear on the next load.
        var menu = new ContextMenu();
        var dup = new MenuItem { Header = "Duplicate to edit" };
        dup.Click += (_, _) => DuplicateForEdit(p);
        menu.Items.Add(dup);
        if (!p.Builtin)
        {
            var del = new MenuItem { Header = "Delete" };
            del.Click += (_, _) => DeleteProfile(p);
            menu.Items.Add(del);
        }
        border.ContextMenu = menu;
        return border;
    }

    private void DuplicateForEdit(StatuslineProfile src)
    {
        var copy = new StatuslineProfile
        {
            Name = UniqueName($"{src.Name} copy"),
            Kind = src.Kind,
            Template = src.Template,
            Command = src.Command,
            Padding = src.Padding,
        };   // Builtin defaults to false → the copy is fully editable
        _config.Profiles.Add(copy);
        RefreshRail();
        SelectProfile(copy);
        Dispatcher.UIThread.Post(() => _nameBox.Focus());   // land in the name field ready to rename
    }

    private void DeleteProfile(StatuslineProfile p)
    {
        if (p.Builtin) return;
        int idx = _config.Profiles.IndexOf(p);
        _config.Profiles.Remove(p);
        if (ReferenceEquals(p, _config.Active)) _config.ActiveName = null;   // Active falls back to first
        if (ReferenceEquals(p, _selected))
        {
            var next = _config.Profiles.ElementAtOrDefault(System.Math.Min(idx, _config.Profiles.Count - 1))
                       ?? _config.Profiles.FirstOrDefault();
            if (next is not null) SelectProfile(next);   // reselect (also refreshes the rail)
        }
        else RefreshRail();
    }

    // ── centre: editor + preview ───────────────────────────────────────────────────────────
    private Control BuildCentre()
    {
        _editorHeader = new TextBlock { Foreground = Muted, FontFamily = Mono, FontSize = 11 };

        _nameBox = new TextBox
        {
            FontSize = 15, FontWeight = FontWeight.SemiBold, Foreground = Fg,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0, 0, 0, 1), BorderBrush = Stroke,
            Padding = new Thickness(0, 2), Margin = new Thickness(0, 2, 0, 8),
        };
        _nameBox.LostFocus += (_, _) => CommitName();

        // toolbar chips (Perch only)
        _chips = new WrapPanel { Orientation = Orientation.Horizontal };
        var chips = _chips;
        foreach (var (label, insert) in new[]
                 {
                     ("{{model.display_name}}", "{{model.display_name}}"),
                     ("{{sep}}", "{{sep}}"),
                     ("#if …", "{{#if EXPR}}{{/if}}"),
                     ("#unless …", "{{#unless EXPR}}{{/unless}}"),
                     ("^ inverted", "{{^PATH}}{{/PATH}}"),
                     ("| bar:10", "| bar:10"),
                     ("| money", "| money"),
                     ("| pct", "| pct"),
                     ("| color:teal", "| color:teal"),
                     ("| color:#hex", "| color:#46c6b8"),
                 })
            chips.Children.Add(Chip(label, insert));

        // The editor is a growing (non-self-scrolling) TextBox beside a line-number gutter, both inside one
        // ScrollViewer so they scroll as a unit — the same trick the session composer uses. Horizontal
        // scrolling is disabled so the box is width-constrained and wraps; the gutter numbers each *logical*
        // line, sized to that line's wrapped height so numbers stay aligned to the first visual row.
        _templateBox = new HighlightTextBox
        {
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            FontFamily = Mono, FontSize = 13, Foreground = TermFg, CaretBrush = TermFg,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Padding = new Thickness(8, EditorPadTop, 8, 8), VerticalAlignment = VerticalAlignment.Top,
            MinHeight = 150,
            // Our minimal HighlightTextBox template carries no Fluent ControlTheme, so the selection brush
            // is otherwise unset (null) and dragging a selection shows nothing. Give it a visible wash; the
            // syntax colours stay legible through the semi-transparent teal.
            SelectionBrush = new SolidColorBrush(Color.FromArgb(0x59, 0x46, 0xc6, 0xb8)),
        };
        // The box neither self-scrolls nor is left to measure its own (under-measured, clipped) height:
        // RebuildGutter measures each wrapped line and sets the box Height explicitly to the exact content
        // height, so it grows precisely with no clipping. The centre column's ScrollViewer scrolls the whole
        // editor when a template is very tall. Both axes Disabled = wrap, no internal scrollbars.
        ScrollViewer.SetHorizontalScrollBarVisibility(_templateBox, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(_templateBox, ScrollBarVisibility.Disabled);
        var hlFace = new Typeface(Mono);
        _templateBox.SetHighlighter(t => StatuslineSourceHighlighter.Highlight(t, hlFace, 13));
        _templateBox.TextChanged += (_, _) =>
        {
            if (_selected.IsPerch && !_selected.Builtin) { _selected.Template = _templateBox.Text ?? ""; }
            if (_selected.IsPerch) UpdatePreview();
            RebuildGutter();
            if (!_suppress && !_selected.Builtin) UpdateCompletions();
        };
        _templateBox.AddHandler(InputElement.KeyDownEvent, OnEditorKeyDown, RoutingStrategies.Tunnel);
        _templateBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == Visual.BoundsProperty) RebuildGutter();
            else if (e.Property == TextBox.CaretIndexProperty || e.Property == InputElement.IsFocusedProperty)
                HighlightPreviewForCaret();
        };

        _gutter = new StackPanel();
        var gutterHost = new Border
        {
            Background = GutterBg, Width = 42, Padding = new Thickness(4, EditorPadTop, 6, 8), Child = _gutter,
        };
        var editorRow = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(gutterHost, Dock.Left);
        editorRow.Children.Add(gutterHost);
        editorRow.Children.Add(_templateBox);

        var editorBorder = new Border
        {
            Background = TermBg, BorderBrush = Stroke, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Child = editorRow, ClipToBounds = true,
        };

        // Autocomplete: an inline list below the editor (no focus stealing) driven by the caret's current
        // token; Tab/Enter accept, ↑/↓ move, Esc dismisses. Each row shows the token and its sample value.
        _completionPanel = new StackPanel();
        _completionHost = new Border
        {
            Background = Panel, BorderBrush = Stroke, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6), Child = _completionPanel, IsVisible = false,
        };

        _softBreakHint = new TextBlock
        {
            Text = "Tip: Shift+Enter inserts a soft line break — it wraps the editor into readable chunks without splitting the status line.",
            Foreground = Muted, FontSize = 11, TextWrapping = TextWrapping.Wrap,
        };

        _perchEditor = new StackPanel { Spacing = 8, Children = { chips, _softBreakHint, editorBorder, _completionHost } };

        // external command editor
        _commandBox = new TextBox
        {
            AcceptsReturn = false, FontFamily = Mono, FontSize = 13, Foreground = Palette.AccentBrush,
            Background = TermBg, BorderBrush = Stroke, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(12),
        };
        _commandBox.TextChanged += (_, _) => { if (!_selected.IsPerch && !_selected.Builtin) { _selected.Command = _commandBox.Text ?? ""; RebuildApplyArea(); } };
        _extEditor = new StackPanel
        {
            Spacing = 8, IsVisible = false,
            Children =
            {
                _commandBox,
                new TextBlock
                {
                    Text = "Imported command — Perch runs it verbatim and shows its output as-is. Kept here so you can switch back any time.",
                    Foreground = Muted, FontSize = 12, TextWrapping = TextWrapping.Wrap,
                },
            },
        };

        // preview — a status line is one long row, so let it scroll horizontally rather than clip the tail.
        _preview = new StackPanel { Spacing = 2 };
        var previewScroll = new ScrollViewer
        {
            Content = _preview,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        var previewBox = new Border
        {
            Background = TermBg, BorderBrush = Stroke, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 12), Child = previewScroll,
        };

        // Apply area: one "Set active" for the usual single-config-dir case, or a row PER writable config
        // dir (each with its own button and a note of what's active there) when a multi-org setup has more
        // than one. Rebuilt on profile change and when the window is re-activated (dirs can be added).
        _applyArea = new StackPanel { Spacing = 6 };

        // Built-in profiles are read-only: this banner replaces the editing affordances and offers a copy.
        var dupBtn = new Button
        {
            Content = "Duplicate to edit", Background = Accent, Foreground = new SolidColorBrush(Color.FromRgb(7, 18, 15)),
            BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 6),
            FontWeight = FontWeight.SemiBold, Cursor = new Cursor(StandardCursorType.Hand),
            VerticalAlignment = VerticalAlignment.Center,
        };
        dupBtn.Click += (_, _) => DuplicateForEdit(_selected);
        var lockDock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(dupBtn, Dock.Right);
        lockDock.Children.Add(dupBtn);
        lockDock.Children.Add(new TextBlock
        {
            Text = "Built-in profile — read-only. Duplicate it to make an editable copy.",
            Foreground = Muted, FontSize = 12, TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        });
        _lockBanner = new Border
        {
            Background = Tint(Accent), BorderBrush = Stroke, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 8), IsVisible = false, Child = lockDock,
        };

        return new StackPanel
        {
            Spacing = 10,
            Children =
            {
                _editorHeader,
                _nameBox,
                _lockBanner,
                _perchEditor,
                _extEditor,
                Eyebrow("Live preview"),
                previewBox,
                new Border { Height = 4 },
                _applyArea,
            },
        };
    }

    // ── right: data explorer ────────────────────────────────────────────────────────────────
    private Control BuildRight()
    {
        var stack = new StackPanel { Spacing = 4, Children = { Eyebrow("Data explorer") } };
        stack.Children.Add(new TextBlock
        {
            Text = "Click a field to insert it. Sample values shown.",
            Foreground = Muted, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
        });

        foreach (var group in StatuslineTokens.Groups)
        {
            stack.Children.Add(new TextBlock
            {
                Text = group.Name, Foreground = Accent, FontFamily = Mono, FontSize = 11,
                FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 10, 0, 2),
            });
            foreach (var tok in group.Tokens)
                stack.Children.Add(TokenRow(tok));
        }

        // A quick reference for the styling filters and the structure tags, with what each does and a
        // click-to-insert. Placing the caret after a token then clicking a filter appends it there.
        stack.Children.Add(RefHeader("Filters — click to add after a token"));
        foreach (var (syntax, desc, insert) in new[]
                 {
                     ("| bar:10", "block progress bar from a 0–100 value", "| bar:10"),
                     ("| pct", "round and add %  ·  34.2 → 34%", "| pct"),
                     ("| round", "round to N decimals, default 0  ·  23.5 → 24", "| round"),
                     ("| round:2", "keep up to N decimals  ·  0.4213 → 0.42", "| round:2"),
                     ("| money", "two decimals  ·  0.4213 → 0.42", "| money"),
                     ("| k", "thousands  ·  68000 → 68k", "| k"),
                     ("| human", "compact count  ·  2500000 → 2M", "| human"),
                     ("| dur", "duration from ms  ·  845000 → 14m", "| dur"),
                     ("| until", "countdown to an epoch reset time", "| until"),
                     ("| upper / lower", "change case", "| upper"),
                     ("| trunc:20", "ellipsize to N characters", "| trunc:20"),
                     ("| default:n/a", "fallback text when the value is empty", "| default:n/a"),
                     ("| color:teal", "named colour (teal/amber/green/red/…)", "| color:teal"),
                     ("| color:#46c6b8", "custom hex colour — any 24-bit truecolor", "| color:#46c6b8"),
                     ("| pace:resets_at:18000", "colour by burn vs. how far through the window", "| pace:resets_at:18000"),
                 })
            stack.Children.Add(RefRow(syntax, desc, insert));

        stack.Children.Add(RefHeader("Structure"));
        foreach (var (syntax, desc, insert) in new[]
                 {
                     ("{{sep}}", "smart divider — vanishes when a neighbour is empty (no doubled/trailing bars)", "{{sep}}"),
                     ("{{sep:·}}", "smart divider with a custom glyph", "{{sep:·}}"),
                     ("{{#if x}}…{{/if}}", "show when x is truthy — also x > 80, s == 'ok'", "{{#if EXPR}}{{/if}}"),
                     ("{{#unless x}}…{{/unless}}", "show when x is falsy", "{{#unless EXPR}}{{/unless}}"),
                     ("{{^x}}…{{/x}}", "inverted — show when x is empty/false", "{{^PATH}}{{/PATH}}"),
                 })
            stack.Children.Add(RefRow(syntax, desc, insert));

        return stack;
    }

    private static TextBlock RefHeader(string text) => new()
    {
        Text = text, Foreground = Accent, FontFamily = Mono, FontSize = 11,
        FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 2),
    };

    private Control RefRow(string syntax, string desc, string insert)
    {
        var stack = new StackPanel
        {
            Spacing = 1,
            Children =
            {
                new TextBlock { Text = syntax, Foreground = Fg, FontFamily = Mono, FontSize = 11.5, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = desc, Foreground = Muted, FontSize = 10.5, TextWrapping = TextWrapping.Wrap },
            },
        };
        var border = new Border
        {
            Padding = new Thickness(8, 5), CornerRadius = new CornerRadius(6),
            Cursor = new Cursor(StandardCursorType.Hand), Background = Brushes.Transparent, Child = stack,
        };
        border.PointerEntered += (_, _) => border.Background = Panel;
        border.PointerExited  += (_, _) => border.Background = Brushes.Transparent;
        border.PointerPressed += (_, _) => Insert(insert);
        return border;
    }

    private Control TokenRow(TokenDescriptor tok)
    {
        var path = new TextBlock { Text = tok.Path, Foreground = Fg, FontFamily = Mono, FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis };
        var val = _sample.TryGet(tok.Path, out var node) ? TemplateData.Str(node) : "";
        if (val.Length > 22) val = val[..21] + "…";
        var value = new TextBlock { Text = val, Foreground = Palette.AccentBrush, FontFamily = Mono, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Right };

        var dock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(value, Dock.Right);
        dock.Children.Add(value);
        if (tok.Badges.Length > 0)
        {
            var badge = new Border
            {
                Background = Panel, CornerRadius = new CornerRadius(4), Padding = new Thickness(4, 0),
                Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = BadgeText(tok.Badges[0]), FontSize = 8.5, FontFamily = Mono, Foreground = Muted },
            };
            DockPanel.SetDock(badge, Dock.Right);
            dock.Children.Add(badge);
        }
        dock.Children.Add(path);

        var border = new Border
        {
            Padding = new Thickness(8, 5), CornerRadius = new CornerRadius(6),
            Cursor = new Cursor(StandardCursorType.Hand), Background = Brushes.Transparent, Child = dock,
        };
        border.PointerEntered += (_, _) => border.Background = Panel;
        border.PointerExited  += (_, _) => border.Background = Brushes.Transparent;
        border.PointerPressed += (_, _) => Insert("{{" + tok.Path + "}}");
        return border;
    }

    /// <summary>Test/preview hook: select a profile by name so <c>HeadlessRenderer</c> can capture a
    /// specific example (e.g. the pace-coloured "Rate-aware verbose"). No-op if the name is unknown.</summary>
    internal void SelectForRender(string name)
    {
        if (_config.Find(name) is { } p) SelectProfile(p);
    }

    /// <summary>Test/preview hook: pose the editor mid-token so the autocomplete list is on screen for a
    /// render capture.</summary>
    internal void ShowCompletionsForRender(string partial)
    {
        var scratch = new StatuslineProfile { Name = "Scratch", Kind = ProfileKind.Perch, Template = partial };
        _config.Profiles.Add(scratch);
        SelectProfile(scratch);   // editable, so the editor + preview reflect the posed text
        _templateBox.CaretIndex = partial.Length;
        UpdateCompletions();
    }

    /// <summary>Test/preview hook: load a template and place the caret so a render capture shows the
    /// caret-driven preview highlight (and any soft breaks). Requires the selected profile be a Perch one.</summary>
    internal void PoseTemplateForRender(string template, int caret)
    {
        // Pose an EDITABLE scratch profile (built-ins are read-only) so the capture shows a normal editing
        // session — the caret-driven highlight, the dimmed siblings and the soft-break hint.
        var scratch = new StatuslineProfile { Name = "Scratch", Kind = ProfileKind.Perch, Template = template };
        _config.Profiles.Add(scratch);
        SelectProfile(scratch);

        // Placing the caret (a click) doesn't pop completions — only typing does. Keep suppression on for
        // the life of this single-use render window so a deferred TextChanged can't reopen the popup.
        _suppress = true;
        _templateBox.Focus();   // the highlight only shows for a focused editor (as when the user clicks in)
        _templateBox.CaretIndex = Math.Clamp(caret, 0, template.Length);
        CloseCompletion();
        UpdatePreview();
        RebuildGutter();
        HighlightPreviewForCaret();
    }


    // ── behaviour ────────────────────────────────────────────────────────────────────────
    private void SelectProfile(StatuslineProfile p)
    {
        _selected = p;
        _nameBox.Text = p.Name;
        _editorHeader.Text = p.IsPerch ? "Template · mustache" : "Imported command · verbatim";
        _perchEditor.IsVisible = p.IsPerch;
        _extEditor.IsVisible = !p.IsPerch;
        if (p.IsPerch) _templateBox.Text = p.Template ?? "";
        else _commandBox.Text = p.Command ?? "";

        // Built-in profiles are locked: editing is disabled and the user is steered to duplicate-to-edit.
        bool locked = p.Builtin;
        _lockBanner.IsVisible = locked;
        _nameBox.IsReadOnly = locked;
        _templateBox.IsReadOnly = locked;
        _commandBox.IsReadOnly = locked;
        _chips.IsVisible = !locked;
        _softBreakHint.IsVisible = !locked;
        if (locked) CloseCompletion();

        RefreshRail();
        UpdatePreview();
        RebuildApplyArea();
    }

    private void UpdatePreview()
    {
        _preview.Children.Clear();
        _previewRuns.Clear();
        if (!_selected.IsPerch)
        {
            _preview.Children.Add(new TextBlock
            {
                Text = "(external command — Perch shows its stdout here unchanged)",
                Foreground = Muted, FontFamily = Mono, FontSize = 12.5,
            });
            return;
        }

        var placed = StatuslineTemplate.RenderPlaced(_selected.Template ?? "", _sample);

        // split into lines on embedded newlines so a multi-line template previews as multiple rows
        var line = new List<Run>();
        void FlushLine()
        {
            var inlines = new InlineCollection();
            if (line.Count == 0) inlines.Add(new Run(" "));
            else foreach (var r in line) inlines.Add(r);
            _preview.Children.Add(new TextBlock { FontFamily = Mono, FontSize = 13, Foreground = TermFg, Inlines = inlines });
            line = new List<Run>();
        }
        foreach (var ps in placed)
        {
            var baseFg = BrushForSeg(ps.Segment);
            var parts = ps.Segment.Text.Split('\n');
            for (int i = 0; i < parts.Length; i++)
            {
                if (i > 0) FlushLine();
                if (parts[i].Length == 0) continue;
                var run = new Run(parts[i]) { Foreground = baseFg };
                line.Add(run);
                // Track EVERY run (with its base colour) so the caret can light one up and dim the rest.
                // Only single-line {{…}} segments are caret targets (IsTag + real source span).
                bool isTag = ps.IsTag && parts.Length == 1;
                _previewRuns.Add(new PreviewRun(run, baseFg,
                    isTag ? ps.SourceStart : -1, isTag ? ps.SourceStart + ps.SourceLength : -1, isTag));
            }
        }
        FlushLine();
        HighlightPreviewForCaret();
    }

    // Light up, in the live preview, the element the editor caret currently sits inside (the rendered
    // output of the {{…}} token spanning the caret) and DIM every other element so it stands out. When the
    // caret isn't in a token (or the editor isn't focused), everything shows at full strength.
    private readonly record struct PreviewRun(Run Run, IBrush BaseFg, int Start, int End, bool IsTag);
    private readonly List<PreviewRun> _previewRuns = new();
    private readonly Dictionary<IBrush, IBrush> _dimCache = new();
    private IBrush? _previewHiBrush;
    private void HighlightPreviewForCaret()
    {
        if (!_selected.IsPerch) return;
        _previewHiBrush ??= new SolidColorBrush(Color.FromArgb(0x40, 0x46, 0xc6, 0xb8));
        // Only while the editor is focused — otherwise the caret resting at 0 on open would light up the
        // first token unprompted. Caret placement is what should reveal the matching preview element.
        int caret = _templateBox.IsFocused ? _templateBox.CaretIndex : -1;
        int match = caret < 0 ? -1 : _previewRuns.FindIndex(r => r.IsTag && caret >= r.Start && caret < r.End);

        for (int i = 0; i < _previewRuns.Count; i++)
        {
            var r = _previewRuns[i];
            if (match < 0)                       // nothing selected → all elements at full strength
            {
                r.Run.Background = null;
                r.Run.Foreground = r.BaseFg;
            }
            else if (i == match)                 // the caret's element: highlighted, full colour
            {
                r.Run.Background = _previewHiBrush;
                r.Run.Foreground = r.BaseFg;
            }
            else                                  // every other element: dimmed
            {
                r.Run.Background = null;
                r.Run.Foreground = Dim(r.BaseFg);
            }
        }
    }

    private IBrush Dim(IBrush b)
    {
        if (_dimCache.TryGetValue(b, out var d)) return d;
        var dimmed = b is ISolidColorBrush s
            ? new SolidColorBrush(Color.FromArgb(0x4D, s.Color.R, s.Color.G, s.Color.B))
            : b;
        return _dimCache[b] = dimmed;
    }

    // ── apply area (per config dir) ───────────────────────────────────────────────────────
    // Rebuilt on profile change / re-activation. One "Set active" for a single config dir, or a labelled row
    // PER config dir (each with its own button + what's active there) for a multi-org setup. EVERY dir in
    // the set is an eligible target: applying a status line is a deliberate per-dir click, so it targets any
    // config dir Perch knows about — auto-detected ones included. (Automatic hook installs are a separate
    // policy in HookInstaller; nothing here is "read-only".)
    private void RebuildApplyArea()
    {
        if (_applyArea is null) return;
        _applyArea.Children.Clear();
        var set = ClaudeConfigSet.Instance;
        _applyDirs = set.All.ToList();

        if (_applyDirs.Count <= 1)
        {
            _applyArea.Children.Add(ApplyDirRow(_applyDirs.Count == 1 ? _applyDirs[0] : set.Primary, showLabel: false));
        }
        else
        {
            _applyArea.Children.Add(Eyebrow("Set active in"));
            foreach (var d in _applyDirs) _applyArea.Children.Add(ApplyDirRow(d, showLabel: true));
            _applyArea.Children.Add(ApplyDirRow(null, showLabel: true));   // the "All directories" row
        }
    }

    // One apply row: a "Set active" button plus (in the multi-dir list) the dir's label and what's active in
    // it now. dir == null is the "All directories" row (applies to every dir at once). When this profile is
    // already active in the dir the button sits in its dimmed "✓ Active" state, so it's obvious at a glance
    // where the profile is and isn't applied (this replaces the old transient "Active ✓" flash).
    private Control ApplyDirRow(ClaudeConfigDir? dir, bool showLabel)
    {
        bool isAll = dir is null;
        var btn = new Button
        {
            CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 7), Margin = new Thickness(8, 0, 14, 0),
            FontWeight = FontWeight.SemiBold, Cursor = new Cursor(StandardCursorType.Hand),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
        };

        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
        if (isAll)
        {
            texts.Children.Add(new TextBlock { Text = "All directories", Foreground = Fg, FontSize = 12.5, FontWeight = FontWeight.SemiBold });
            texts.Children.Add(new TextBlock { Text = $"apply to every directory ({_applyDirs.Count})", Foreground = Muted, FontSize = 10.5 });
            StyleApplyButton(btn, _applyDirs.Count > 0 && _applyDirs.All(d => DirStatus(d).IsThisProfile));
            btn.Click += (_, _) => { if (ApplyProfileTo(_applyDirs)) RebuildApplyArea(); else Flash(btn, "Failed"); };
        }
        else
        {
            if (showLabel)
                texts.Children.Add(new TextBlock { Text = DirComboLabel(dir!), Foreground = Fg, FontSize = 12.5, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.PrefixCharacterEllipsis });
            else
                texts.Children.Add(new TextBlock { Text = $"Writes {Tildify(dir!.UserSettingsFile)}", Foreground = Muted, FontSize = 11, TextWrapping = TextWrapping.Wrap });

            var (statusText, isThis) = DirStatus(dir!);
            var status = new TextBlock { Text = statusText, Foreground = isThis ? Accent : Muted, FontSize = 10.5, FontFamily = Mono, TextTrimming = TextTrimming.CharacterEllipsis };
            texts.Children.Add(status);
            StyleApplyButton(btn, isThis);

            btn.Click += (_, _) =>
            {
                if (ApplyProfileTo(new List<ClaudeConfigDir> { dir! }))
                {
                    var (t, isNow) = DirStatus(dir!);
                    status.Text = t; status.Foreground = isNow ? Accent : Muted;
                    StyleApplyButton(btn, isNow);
                }
                else Flash(btn, "Failed");
            };
        }

        var dock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(btn, Dock.Right);
        dock.Children.Add(btn);
        dock.Children.Add(texts);
        return new Border { Padding = new Thickness(0, 3), Child = dock };
    }

    // Fill+dark when actionable ("Set active"); dimmed outline with a tick when this profile is already the
    // active one in that dir. Kept clickable while applied so re-applying after an edit still works.
    private static readonly IBrush ApplyBtnFg = new SolidColorBrush(Color.FromRgb(7, 18, 15));
    private void StyleApplyButton(Button b, bool applied)
    {
        b.Content = applied ? "✓ Active" : "Set active";
        b.Background = applied ? Tint(Accent) : Accent;
        b.Foreground = applied ? Accent : ApplyBtnFg;
        b.BorderBrush = applied ? Accent : Brushes.Transparent;
        b.BorderThickness = new Thickness(applied ? 1 : 0);
        b.Opacity = applied ? 0.9 : 1.0;
    }

    // What's currently active in a config dir, and whether it's THIS profile — read from its settings.json
    // (and, for a Perch line, the profile name baked into its generated script's header).
    private (string Text, bool IsThisProfile) DirStatus(ClaudeConfigDir dir)
    {
        var cmd = StatuslineInstaller.CurrentCommand(dir);
        if (string.IsNullOrWhiteSpace(cmd)) return ("— no status line set", false);

        var perchCmd = StatuslineScript.CommandFor(StatuslineScript.ScriptPathFor(dir.Root));
        if (string.Equals(cmd, perchCmd, StringComparison.OrdinalIgnoreCase))
        {
            var name = StatuslineScript.ProfileNameFromScript(StatuslineScript.ScriptPathFor(dir.Root));
            if (name is null) return ("active: a Perch status line", false);
            bool isThis = string.Equals(name, _selected.Name, StringComparison.Ordinal);
            return (isThis ? $"active: {name}  ✓" : $"active: {name}", isThis);
        }
        return ("active: " + (cmd.Length > 44 ? cmd[..43] + "…" : cmd), false);
    }

    // Applies the selected profile to each target dir; persists the library + active name. Returns false if
    // any write failed. No UI side effects — the caller flashes / refreshes.
    private bool ApplyProfileTo(List<ClaudeConfigDir> targets)
    {
        if (targets.Count == 0) return false;
        CommitName();
        _config.ActiveName = _selected.Name;
        StatuslineStore.Save(_config);
        bool ok = true;
        foreach (var dir in targets)
            ok &= StatuslineInstaller.Apply(_selected, dir, out _);
        if (ok) RefreshRail();
        return ok;
    }

    private static string DirComboLabel(ClaudeConfigDir d) =>
        d.Provenance == ConfigDirProvenance.Primary
            ? $"Default  ·  {Tildify(d.Root)}"
            : $"{(string.IsNullOrWhiteSpace(d.CustomLabel) ? d.Label : d.CustomLabel)}  ·  {Tildify(d.Root)}";

    private static string Tildify(string path)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return !string.IsNullOrEmpty(home) && path.StartsWith(home, StringComparison.OrdinalIgnoreCase)
            ? "~" + path[home.Length..].Replace('\\', '/')
            : path;
    }

    private void CommitName()
    {
        if (_selected.Builtin) { _nameBox.Text = _selected.Name; return; }   // built-ins are read-only
        var name = (_nameBox.Text ?? "").Trim();
        if (name.Length == 0 || name == _selected.Name) { _nameBox.Text = _selected.Name; return; }
        // reject a collision with a different profile
        if (_config.Profiles.Any(q => !ReferenceEquals(q, _selected) && string.Equals(q.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            _nameBox.Text = _selected.Name;
            return;
        }
        var wasActive = ReferenceEquals(_selected, _config.Active);
        _selected.Name = name;
        if (wasActive) _config.ActiveName = name;
        RefreshRail();
        RebuildApplyArea();
    }

    private void Insert(string text)
    {
        if (!_selected.IsPerch || _templateBox.IsReadOnly) return;   // never mutate a locked built-in
        var box = _templateBox;
        int at = Math.Clamp(box.CaretIndex, 0, (box.Text ?? "").Length);
        var t = box.Text ?? "";
        box.Text = t[..at] + text + t[at..];
        box.CaretIndex = at + text.Length;
        box.Focus();
    }

    // ── line-number gutter ───────────────────────────────────────────────────────────────
    // One number per logical line, each sized to that line's *wrapped* height (measured with the editor's
    // own font at its current text width) so a number sits beside the first visual row of its line and the
    // continuation rows stay blank.
    private void RebuildGutter()
    {
        if (_gutter is null) return;

        var text = _templateBox.Text ?? "";
        var lines = text.Split('\n');
        double width = _templateBox.Bounds.Width - 16;   // minus the 8+8 horizontal padding
        if (width <= 10) return;   // not arranged yet; a later Bounds change re-runs this with a real width

        var typeface = new Typeface(Mono);
        _gutter.Children.Clear();
        double total = 0;
        int lineNo = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            var layout = new TextLayout(lines[i].Length == 0 ? " " : lines[i], typeface, 13, Muted,
                textAlignment: TextAlignment.Left, textWrapping: TextWrapping.Wrap, maxWidth: width);
            double h = layout.Height;
            total += h;

            // A physical line whose predecessor ended with a soft break ("\") is a continuation of the same
            // status line, not a new one — mark it with a dim glyph and don't advance the line number.
            bool continuation = i > 0 && lines[i - 1].EndsWith("\\", StringComparison.Ordinal);
            if (!continuation) lineNo++;
            _gutter.Children.Add(new TextBlock
            {
                Text = continuation ? "·" : lineNo.ToString(), Height = h, FontFamily = Mono, FontSize = 13,
                Foreground = Muted, Opacity = continuation ? 0.5 : 1, TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
            });
        }

        // Drive the box height from a whole-text measurement (exactly how the presenter lays it out) plus a
        // full extra line of slack, so the final wrapped row can never be clipped (Avalonia's own wrapped
        // measure runs ~a line short). The centre column scrolls if the whole editor grows tall.
        var whole = new TextLayout(text.Length == 0 ? " " : text, typeface, 13, Muted,
            textAlignment: TextAlignment.Left, textWrapping: TextWrapping.Wrap, maxWidth: width);
        double lineH = whole.TextLines.Count > 0 ? whole.Height / whole.TextLines.Count : 18;
        double content = System.Math.Max(total, whole.Height);
        double target = System.Math.Max(150, content + EditorPadTop + 8 + lineH);
        if (double.IsNaN(_templateBox.Height) || System.Math.Abs(_templateBox.Height - target) > 0.5)
            _templateBox.Height = target;
    }

    // ── autocomplete ─────────────────────────────────────────────────────────────────────
    private void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        // Shift+Enter inserts a *soft break*: a "\" + newline the editor wraps for readability but the
        // renderer joins back into one line (see StatuslineTemplate.StripSoftBreaks). Plain Enter still
        // makes a real line break. Handled here so the base TextBox doesn't also insert a bare newline.
        if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Shift) && _selected.IsPerch && !_templateBox.IsReadOnly)
        {
            if (_completionHost.IsVisible) CloseCompletion();
            Insert("\\\n");
            e.Handled = true;
            return;
        }

        if (!_completionHost.IsVisible) return;
        switch (e.Key)
        {
            case Key.Down:   MoveCompletion(1);  e.Handled = true; break;
            case Key.Up:     MoveCompletion(-1); e.Handled = true; break;
            case Key.Enter:
            case Key.Tab:    AcceptCompletion(); e.Handled = true; break;
            case Key.Escape: CloseCompletion();  e.Handled = true; break;
        }
    }

    // What the caret is positioned to complete (kind + the partial word + where it starts), or null when
    // the caret isn't inside an open {{ … }} or is somewhere we don't offer completions (a numeric arg).
    private (CompKind Kind, string Word, int Start)? CurrentCompletion()
    {
        var text = _templateBox.Text ?? "";
        int caret = Math.Clamp(_templateBox.CaretIndex, 0, text.Length);

        int open = -1;
        for (int i = Math.Min(caret, text.Length) - 2; i >= 0; i--)
        {
            if (text[i] == '{' && text[i + 1] == '{') { open = i; break; }
            if (text[i] == '}' && text[i + 1] == '}') break;
        }
        if (open < 0) return null;
        int content = open + 2;

        // In the filter part? (a | between the block open and the caret)
        int lastBar = -1;
        for (int i = content; i < caret; i++) if (text[i] == '|') lastBar = i;
        if (lastBar >= 0)
        {
            int colon = -1;
            for (int i = lastBar + 1; i < caret; i++) if (text[i] == ':') colon = i;
            if (colon >= 0)
            {
                // an arg — only color: completes (to a colour name); numeric args don't
                if (!text[(lastBar + 1)..colon].Trim().Equals("color", StringComparison.OrdinalIgnoreCase))
                    return null;
                int p = colon + 1; while (p < caret && char.IsWhiteSpace(text[p])) p++;
                return (CompKind.Color, text[p..caret], p);
            }
            int q = lastBar + 1; while (q < caret && char.IsWhiteSpace(text[q])) q++;
            return (CompKind.Filter, text[q..caret], q);
        }

        // The head: a field path, or the if/unless keyword right after a {{# sigil.
        int s = caret;
        while (s > content && (char.IsLetterOrDigit(text[s - 1]) || text[s - 1] == '_' || text[s - 1] == '.')) s--;
        var kind = text[content..s].Trim() == "#" ? CompKind.Section : CompKind.Path;
        return (kind, text[s..caret], s);
    }

    private void UpdateCompletions()
    {
        if (!_selected.IsPerch || CurrentCompletion() is not { } c) { CloseCompletion(); return; }
        var (kind, word, start) = c;
        var q = word.ToLowerInvariant();

        var matches = kind switch
        {
            CompKind.Filter => FilterCatalog
                .Where(f => q.Length == 0 || f.Name.Contains(q))
                .OrderByDescending(f => f.Name.StartsWith(q, StringComparison.Ordinal)).ThenBy(f => f.Name)
                .Select(f => (f.Name, Value: f.Sig)).ToList(),
            CompKind.Color => StatusColors.Names
                .Where(cn => q.Length == 0 || cn.Contains(q))
                .OrderByDescending(cn => cn.StartsWith(q, StringComparison.Ordinal)).ThenBy(cn => cn)
                .Select(cn => (cn, Value: "")).ToList(),
            CompKind.Section => new[] { ("if", "conditional"), ("unless", "conditional") }
                .Where(k => q.Length == 0 || k.Item1.Contains(q)).Select(k => (k.Item1, Value: k.Item2)).ToList(),
            _ => StatuslineTokens.Groups.SelectMany(g => g.Tokens)
                .Where(t => q.Length == 0 || t.Path.ToLowerInvariant().Contains(q))
                .OrderByDescending(t => t.Path.StartsWith(word, StringComparison.OrdinalIgnoreCase))
                .ThenBy(t => t.Path, StringComparer.Ordinal)
                .Take(8)
                .Select(t => (t.Path, Value: SampleValue(t.Path))).ToList(),
        };

        // Nothing to offer, or the word is already the one exact match — don't nag.
        if (matches.Count == 0 || (matches.Count == 1 && matches[0].Item1 == word)) { CloseCompletion(); return; }

        _completions = matches;
        _completionKind = kind;
        _completionStart = start;
        _completionLen = word.Length;
        _completionIndex = 0;
        RebuildCompletionRows();
        _completionHost.IsVisible = true;
    }

    private void RebuildCompletionRows()
    {
        _completionPanel.Children.Clear();
        for (int i = 0; i < _completions.Count; i++)
        {
            var (label, val) = _completions[i];

            // A colour name is drawn in the colour it names, as a swatch.
            IBrush labelBrush = Fg;
            if (_completionKind == CompKind.Color && StatusColors.Parse(label) is var role && role != StatusColor.Default)
            {
                var (r, g, b) = StatusColors.Rgb(role);
                labelBrush = new SolidColorBrush(Color.FromRgb(r, g, b));
            }

            var dock = new DockPanel { LastChildFill = true };
            var valText = new TextBlock
            {
                Text = val, FontFamily = Mono, FontSize = 11, Foreground = Muted,
                HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(10, 0, 0, 0),
            };
            DockPanel.SetDock(valText, Dock.Right);
            dock.Children.Add(valText);
            dock.Children.Add(new TextBlock { Text = label, FontFamily = Mono, FontSize = 12, Foreground = labelBrush });

            int idx = i;
            var row = new Border
            {
                Padding = new Thickness(9, 5), Child = dock,
                Background = i == _completionIndex ? Tint(Accent) : Brushes.Transparent,
                Cursor = new Cursor(StandardCursorType.Hand),
            };
            row.PointerPressed += (_, _) => { _completionIndex = idx; AcceptCompletion(); };
            _completionPanel.Children.Add(row);
        }
    }

    private void MoveCompletion(int delta)
    {
        if (_completions.Count == 0) return;
        _completionIndex = Math.Clamp(_completionIndex + delta, 0, _completions.Count - 1);
        RebuildCompletionRows();
    }

    private void AcceptCompletion()
    {
        if (_completions.Count == 0) { CloseCompletion(); return; }
        var path = _completions[Math.Clamp(_completionIndex, 0, _completions.Count - 1)].Path;
        var t = _templateBox.Text ?? "";
        int end = Math.Min(_completionStart + _completionLen, t.Length);

        _suppress = true;
        _templateBox.Text = t[.._completionStart] + path + t[end..];
        _templateBox.CaretIndex = _completionStart + path.Length;
        _suppress = false;

        CloseCompletion();
        _templateBox.Focus();
    }

    private void CloseCompletion()
    {
        if (_completionHost is not null) _completionHost.IsVisible = false;
    }

    private string SampleValue(string path)
    {
        var v = _sample.TryGet(path, out var n) ? TemplateData.Str(n) : "";
        return v.Length > 24 ? v[..23] + "…" : v;
    }

    // ── small builders / helpers ─────────────────────────────────────────────────────────────
    private static TextBlock Eyebrow(string text) => new()
    {
        Text = text.ToUpperInvariant(), Foreground = Muted, FontFamily = Mono, FontSize = 10.5,
        FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 4),
    };

    private static Control DivLabel(string text) => new TextBlock
    {
        Text = text.ToUpperInvariant(), Foreground = Palette.MutedBrush, FontFamily = Mono, FontSize = 9.5,
        Margin = new Thickness(0, 12, 0, 2),
    };

    private Button Chip(string label, string insert)
    {
        var b = new Button
        {
            Content = label, FontFamily = Mono, FontSize = 11, Foreground = Fg,
            Background = Panel, BorderBrush = Stroke, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 4), Margin = new Thickness(0, 0, 6, 6),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        b.Click += (_, _) => Insert(insert);
        return b;
    }

    private static Button TextButton(string label, Action onClick)
    {
        var b = new Button
        {
            Content = label, FontSize = 12.5, FontWeight = FontWeight.SemiBold, Foreground = Fg,
            Background = Palette.ButtonBgBrush, BorderBrush = Palette.BorderBrush, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(11, 9), Margin = new Thickness(0, 0, 0, 1),
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        b.Click += (_, _) => onClick();
        return b;
    }

    private string UniqueName(string baseName)
    {
        if (_config.Find(baseName) is null) return baseName;
        for (int i = 2; ; i++)
        {
            var candidate = $"{baseName} ({i})";
            if (_config.Find(candidate) is null) return candidate;
        }
    }

    private static string BadgeText(TokenBadge b) => b switch
    {
        TokenBadge.NullEarly    => "null early",
        TokenBadge.VersionGated => "v2.1.251+",
        TokenBadge.PerchExtra   => "Perch",
        TokenBadge.WhenPresent  => "when present",
        _ => "",
    };

    private readonly Dictionary<StatusColor, IBrush> _brushes = new();
    private IBrush BrushFor(StatusColor c)
    {
        if (c == StatusColor.Default) return TermFg;
        if (_brushes.TryGetValue(c, out var b)) return b;
        var (r, g, bl) = StatusColors.Rgb(c);
        return _brushes[c] = new SolidColorBrush(Color.FromRgb(r, g, bl));
    }

    // A rendered segment's brush: a custom hex RGB (color:#rrggbb) wins over the named role.
    private readonly Dictionary<int, IBrush> _rgbBrushes = new();
    private IBrush BrushForSeg(StatuslineSegment seg)
    {
        if (seg.Rgb < 0) return BrushFor(seg.Color);
        if (_rgbBrushes.TryGetValue(seg.Rgb, out var b)) return b;
        var c = Color.FromRgb((byte)((seg.Rgb >> 16) & 0xFF), (byte)((seg.Rgb >> 8) & 0xFF), (byte)(seg.Rgb & 0xFF));
        return _rgbBrushes[seg.Rgb] = new SolidColorBrush(c);
    }

    private static IBrush Tint(IBrush accent) =>
        accent is ISolidColorBrush s
            ? new SolidColorBrush(Color.FromArgb(0x22, s.Color.R, s.Color.G, s.Color.B))
            : Palette.ButtonBgBrush;

    private void Flash(Control target, string message)
    {
        if (target is Button btn)
        {
            var old = btn.Content;
            btn.Content = message;
            _appliedTimer?.Stop();
            _appliedTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1400) };
            _appliedTimer.Tick += (_, _) => { _appliedTimer!.Stop(); btn.Content = old; };
            _appliedTimer.Start();
        }
        else if (target is TextBlock tb)
        {
            var old = tb.Text;
            tb.Text = message;
            _appliedTimer?.Stop();
            _appliedTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1800) };
            _appliedTimer.Tick += (_, _) => { _appliedTimer!.Stop(); tb.Text = old; };
            _appliedTimer.Start();
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        base.OnKeyDown(e);
    }
}

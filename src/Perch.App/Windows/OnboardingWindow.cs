using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Perch.Avalonia.Rendering;
using Perch.Avalonia.Services;
using Perch.Avalonia.Theming;
using Perch.Avalonia.Views;
using Perch.Data;

namespace Perch.Avalonia.Windows;

/// <summary>
/// The first-run guided Quick Start (see docs/onboarding-quickstart-plan.md). A five-step wizard —
/// Welcome, the overlay layout tour, Floating-vs-Docked placement, the feature-tier chooser, and a
/// summary — that seeds a sensible starting set of features instead of dropping a new user into all
/// 60-odd settings at once. Tiers are presets over a fine-tunable set: the chooser's chips each toggle
/// one feature. On Finish it writes the chosen set through <see cref="OnboardingTiers"/>, stamps
/// <see cref="AppSettings.FirstRunComplete"/>, saves, and invokes <c>onApplied</c> so the app pushes the
/// change live. Built in code, themed through <see cref="Palette"/>, in the house window style.
/// </summary>
internal sealed class OnboardingWindow : Window
{
    private enum StepKind { Welcome, Tour, Placement, Tiers, Done }

    private static (string Title, string Sub) StepInfo(StepKind k) => k switch
    {
        StepKind.Welcome   => ("Welcome", "What Perch does"),
        StepKind.Tour      => ("The lay of the land", "Read the overlay"),
        StepKind.Placement => ("Where it lives", "Floating or docked"),
        StepKind.Tiers     => ("Choose your setup", "How much to switch on"),
        _                  => ("You're all set", "Where things live"),
    };

    private readonly AppSettings _settings;
    private readonly bool _dockedSupported;
    private readonly bool _quietActive;
    private readonly Action _onApplied;

    // Working state, kept across step navigation (the stage is rebuilt each GoTo).
    private readonly HashSet<string> _enabled;
    private readonly List<StepKind> _steps;   // the ordered sequence (placement dropped where docking is unsupported)
    private OnboardingTier _preset;
    private OverlayPresentationMode _placement;
    private int _step;

    private int LastStep => _steps.Count - 1;
    private StepKind Current => _steps[_step];

    // Chrome refs updated as steps/state change.
    private readonly ContentControl _stage = new();
    private readonly TextBlock _stepCount = new();
    private readonly Button _backBtn = new();
    private readonly Button _nextBtn = new();
    private readonly List<Button> _railButtons = new();

    // Live tier-step refs (valid only while step 3 is shown).
    private StackPanel? _breakdownHost;
    private TextBlock? _tallyText;
    private TextBlock? _breakdownLead;
    private Button? _resetBtn;
    private TextBlock? _quietNote;
    private readonly List<(OnboardingTier Tier, Border Card)> _tierCards = new();

    // ── Palette-derived fills (a transient window; snapshotted at construction) ──
    private readonly IBrush _accent = new SolidColorBrush(Palette.Accent);
    private readonly IBrush _onAccent = new SolidColorBrush(Palette.OnAccent);
    private readonly IBrush _on = new SolidColorBrush(Palette.Green);
    private readonly IBrush _text = new SolidColorBrush(Palette.Fg);
    private readonly IBrush _muted = new SolidColorBrush(Palette.Muted);
    private readonly IBrush _border = new SolidColorBrush(Palette.Border);
    private readonly IBrush _surface = new SolidColorBrush(Palette.FormBg);
    private readonly IBrush _button = new SolidColorBrush(Palette.ButtonBg);
    private readonly IBrush _accentSoft;
    private readonly IBrush _onSoft;

    public OnboardingWindow(AppSettings settings, bool dockedSupported, Action onApplied, bool quietActive = false)
    {
        _settings = settings;
        _dockedSupported = dockedSupported;
        _quietActive = quietActive;
        _onApplied = onApplied;

        _accentSoft = new SolidColorBrush(Soft(Palette.Accent, 40));
        _onSoft = new SolidColorBrush(Soft(Palette.Green, 34));

        _preset = settings.OnboardingTierChosen ?? OnboardingTier.Intermediate;
        _enabled = OnboardingTiers.Preset(_preset).ToHashSet();
        _placement = dockedSupported ? settings.OverlayMode : OverlayPresentationMode.Floating;

        // The placement step only makes sense where the overlay can actually dock (Windows). On macOS the
        // overlay always floats, so drop that step entirely rather than offering a one-option choice.
        _steps = dockedSupported
            ? [StepKind.Welcome, StepKind.Tour, StepKind.Placement, StepKind.Tiers, StepKind.Done]
            : [StepKind.Welcome, StepKind.Tour, StepKind.Tiers, StepKind.Done];

        Title = "Welcome to Perch";
        Width = 940;
        Height = 660;
        MinWidth = 780;
        MinHeight = 560;
        Background = Palette.SurfaceSunkenBrush;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        Content = BuildChrome();
        GoTo(0);
    }

    private static Color Soft(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    // The Perch brand mark (the app icon), loaded once. Falls back to the bird emoji if the asset is missing.
    private static Bitmap? _logo;
    private static bool _logoTried;
    private static Control LogoMark(double size)
    {
        if (!_logoTried)
        {
            _logoTried = true;
            try { _logo = new Bitmap(AssetLoader.Open(new Uri("avares://perch/Assets/icon.png"))); }
            catch { _logo = null; }
        }
        return _logo is { } b
            ? new Image { Source = b, Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center }
            : new TextBlock { Text = "🐦", FontSize = size * 0.85, VerticalAlignment = VerticalAlignment.Center };
    }

    /// <summary>Navigates to a given step for the headless renderer (HeadlessRenderer shows the window and
    /// captures a frame; templated controls only get their styles inside a shown window).</summary>
    public void ShowStepForRender(int step) => GoTo(step);

    // ── Chrome (header / rail / stage / footer) ──────────────────────────────

    private Control BuildChrome()
    {
        var header = new Border
        {
            Background = _surface,
            BorderBrush = _border,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(18, 12),
            Child = new DockPanel
            {
                LastChildFill = false,
                Children =
                {
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal, Spacing = 9,
                        VerticalAlignment = VerticalAlignment.Center,
                        Children =
                        {
                            LogoMark(20),
                            new TextBlock
                            {
                                Text = "Perch  ·  Quick start", FontSize = 14, FontWeight = FontWeight.SemiBold,
                                Foreground = _text, VerticalAlignment = VerticalAlignment.Center,
                            },
                        },
                    },
                    WithDock(_stepCount, Dock.Right),
                },
            },
        };
        _stepCount.Foreground = _muted;
        _stepCount.FontSize = 12;
        _stepCount.VerticalAlignment = VerticalAlignment.Center;

        var rail = new Border
        {
            Width = 224,
            Background = _surface,
            BorderBrush = _border,
            BorderThickness = new Thickness(0, 0, 1, 0),
            Padding = new Thickness(14, 20),
            Child = BuildRail(),
            [DockPanel.DockProperty] = Dock.Left,
        };

        var stageScroll = new ScrollViewer
        {
            Content = _stage,
            Padding = new Thickness(30, 26),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };

        var body = new DockPanel { Children = { rail, stageScroll } };

        return new DockPanel
        {
            Background = Palette.SurfaceSunkenBrush,
            Children = { WithDock(header, Dock.Top), WithDock(BuildFooter(), Dock.Bottom), body },
        };
    }

    private Control BuildRail()
    {
        var stack = new StackPanel { Spacing = 3 };
        stack.Children.Add(new TextBlock
        {
            Text = "GETTING STARTED", FontSize = 11, FontWeight = FontWeight.SemiBold,
            Foreground = _muted, Margin = new Thickness(6, 0, 0, 12),
        });

        for (var i = 0; i < _steps.Count; i++)
        {
            var idx = i;
            var (title, sub) = StepInfo(_steps[i]);
            var btn = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(11, 9),
                CornerRadius = new CornerRadius(10),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 11,
                    Children =
                    {
                        StepDot(idx),
                        new StackPanel
                        {
                            VerticalAlignment = VerticalAlignment.Center,
                            Children =
                            {
                                new TextBlock { Text = title, FontSize = 13.5, FontWeight = FontWeight.SemiBold, Foreground = _text },
                                new TextBlock { Text = sub, FontSize = 11, Foreground = _muted },
                            },
                        },
                    },
                },
            };
            btn.Click += (_, _) => GoTo(idx);
            _railButtons.Add(btn);
            stack.Children.Add(btn);
        }

        return stack;
    }

    private Border StepDot(int idx) => new()
    {
        Width = 24, Height = 24, CornerRadius = new CornerRadius(12),
        Background = _button, BorderBrush = _border, BorderThickness = new Thickness(1),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock
        {
            Text = (idx + 1).ToString(), FontSize = 12, FontWeight = FontWeight.Bold, Foreground = _muted,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        },
    };

    private Control BuildFooter()
    {
        var skip = GhostButton("Skip setup");
        skip.Click += (_, _) => { Commit(applyTier: false); };

        _backBtn.Content = "Back";
        _backBtn.MinWidth = 92;
        _backBtn.Padding = new Thickness(16, 9);
        _backBtn.CornerRadius = new CornerRadius(9);
        _backBtn.HorizontalContentAlignment = HorizontalAlignment.Center;
        _backBtn.VerticalContentAlignment = VerticalAlignment.Center;
        _backBtn.Click += (_, _) => GoTo(_step - 1);

        StylePrimary(_nextBtn);
        _nextBtn.IsDefault = true;   // Enter advances / finishes
        _nextBtn.Click += (_, _) => OnNext();

        var right = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 10,
            [DockPanel.DockProperty] = Dock.Right,
            Children = { _backBtn, _nextBtn },
        };

        return new Border
        {
            Background = _surface,
            BorderBrush = _border,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(18, 12),
            Child = new DockPanel { LastChildFill = false, Children = { right, WithDock(skip, Dock.Left) } },
        };
    }

    // ── Navigation ───────────────────────────────────────────────────────────

    private void GoTo(int n)
    {
        _step = Math.Clamp(n, 0, LastStep);
        _stage.Content = Current switch
        {
            StepKind.Welcome   => BuildWelcome(),
            StepKind.Tour      => BuildLayoutTour(),
            StepKind.Placement => BuildPlacement(),
            StepKind.Tiers     => BuildTierChooser(),
            _                  => BuildDone(),
        };

        _stepCount.Text = $"Step {_step + 1} of {_steps.Count}";
        _backBtn.IsEnabled = _step > 0;
        _nextBtn.Content = NextLabel();

        for (var i = 0; i < _railButtons.Count; i++)
            _railButtons[i].Background = i == _step ? _button : Brushes.Transparent;
    }

    private string NextLabel()
    {
        if (_step == LastStep) return "Finish setup";
        if (Current == StepKind.Tiers)
            return OnboardingTiers.MatchedTier(_enabled) is { } t ? $"Use {TierName(t)}" : "Use custom setup";
        return "Next";
    }

    private void OnNext()
    {
        if (_step == LastStep) { Commit(applyTier: true); return; }
        GoTo(_step + 1);
    }

    // Persists the choices and signals the app to apply them live, then closes. applyTier=false is the
    // "Skip setup" path: it only records that onboarding was seen (so it won't reappear) and applies nothing.
    private void Commit(bool applyTier)
    {
        if (applyTier)
        {
            OnboardingTiers.Apply(_settings, _enabled);
            _settings.OverlayMode = _placement;
            _settings.OnboardingTierChosen = OnboardingTiers.MatchedTier(_enabled);
        }

        _settings.FirstRunComplete = true;
        _settings.Save();
        _onApplied();
        Close();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Left/Right page through the wizard; Escape closes. Enter is handled by the default Next button.
        // Guarded on !Handled so a focused control (a button, a future focusable chip) wins first.
        if (!e.Handled)
        {
            switch (e.Key)
            {
                case Key.Escape: Close(); e.Handled = true; break;
                case Key.Right:  GoTo(_step + 1); e.Handled = true; break;
                case Key.Left:   GoTo(_step - 1); e.Handled = true; break;
            }
        }
        base.OnKeyDown(e);
    }

    // ── Step 0: Welcome ──────────────────────────────────────────────────────

    private Control BuildWelcome()
    {
        var panel = new StackPanel { Spacing = 14, MaxWidth = 600, HorizontalAlignment = HorizontalAlignment.Left };
        panel.Children.Add(Eyebrow("NICE TO MEET YOU"));
        panel.Children.Add(H1("A quiet roost for your Claude Code sessions."));
        panel.Children.Add(Lede(
            "Perch sits in your system tray and keeps an eye on every running session — surfacing status, " +
            "usage and nudges as a small desktop overlay, so you don't have to keep flipping back to check."));

        panel.Children.Add(Fact("👁️", "It watches, you work.",
            "A glance tells you which sessions are running, waiting on you, or stuck."));
        panel.Children.Add(Fact("🔔", "It taps you on the shoulder.",
            "Optional toasts and chimes when a run finishes or needs your input."));
        panel.Children.Add(Fact("🎚️", "It grows with you.",
            "Perch has a lot under the hood — we'll start light, and you can add more whenever."));

        panel.Children.Add(new TextBlock
        {
            Text = "Takes about a minute. Nothing here is permanent — every switch has a home in Settings afterwards.",
            FontSize = 12.5, Foreground = _muted, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap,
        });
        return panel;
    }

    // ── Step 1: Layout tour ──────────────────────────────────────────────────

    private Control BuildLayoutTour()
    {
        var root = new StackPanel { Spacing = 14, MaxWidth = 680, HorizontalAlignment = HorizontalAlignment.Left };
        root.Children.Add(Eyebrow("THE LAY OF THE LAND"));
        root.Children.Add(H1("This little panel is your overlay."));
        root.Children.Add(Lede(
            "It floats at the edge of your screen (or docks to a column) whenever a session is running. " +
            "Here's what you'll find on it — shown live on the right:"));

        var points = new StackPanel { Spacing = 12, VerticalAlignment = VerticalAlignment.Top };
        points.Children.Add(TourRow("1", "Header & the bird",
            "Drag to move it; right-click for Quiet mode and placement. The bird mirrors the overall mood."));
        points.Children.Add(TourRow("2", "System & usage",
            "Machine CPU/RAM, then your 5-hour and weekly rate-limit bars — headroom at a glance."));
        points.Children.Add(TourRow("3", "Quick links",
            "One-tap launchers — GitHub, Jira, a scratch note, whatever you pin."));
        points.Children.Add(TourRow("4", "Session rows",
            "One row per live session: a status dot, the project, and badges like permission mode or a waiting timer."));
        points.Children.Add(TourRow("5", "Movable sections",
            "Todos, media and more stack in an order you choose — rearrange them in Settings."));

        var previewCol = new StackPanel
        {
            Spacing = 8, Width = 272, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(20, 2, 0, 0),
            [DockPanel.DockProperty] = Dock.Right,
        };
        previewCol.Children.Add(new TextBlock
        {
            Text = "LIVE PREVIEW", FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = _muted,
            Margin = new Thickness(2, 0, 0, 0),
        });
        previewCol.Children.Add(TourPreview());

        var row = new DockPanel { LastChildFill = true, Children = { previewCol, points } };
        root.Children.Add(row);
        return root;
    }

    // A miniature, display-only overlay showing the features the tour points call out — the same
    // OverlayCanvas + SampleData + OverlaySettingsGates path the Settings live preview uses (PreviewPane),
    // seeded here so quick links and the movable sections the tour mentions are present.
    private Control TourPreview()
    {
        var canvas = new OverlayCanvas();
        canvas.Update(SampleData.Sessions());
        canvas.UpdateUsage(SampleData.Usage());
        canvas.UpdateSystemMetrics(SampleData.SystemMetrics());
        canvas.UpdateSessionMetrics(SampleData.SessionMetrics());
        canvas.SetTopTodos(SampleData.Todos(), SampleData.Todos().Count);
        canvas.UpdateMedia(SampleData.Media());
        canvas.SetQuickLinks(
            [new QuickLink { Name = "GitHub" }, new QuickLink { Name = "Jira" }, new QuickLink { Name = "Slack" }],
            [null, null, null]);

        // Turn on exactly the tour's features (session-row badges are on by default).
        OverlaySettingsGates.Apply(canvas, new AppSettings
        {
            ShowSystemMetrics = true,
            ShowUsage = true,
            ShowExpectedUsageRate = true,
            ShowTodos = true,
            ShowMediaController = true,
        });
        canvas.IsHitTestVisible = false;   // a picture, not a control

        return new Border
        {
            Background = _button, CornerRadius = new CornerRadius(12),
            BorderBrush = _border, BorderThickness = new Thickness(1), Padding = new Thickness(10),
            Child = new Viewbox
            {
                Child = canvas, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly,
                Width = 250, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
            },
        };
    }

    // ── Step 2: Placement ────────────────────────────────────────────────────

    private Control BuildPlacement()
    {
        var panel = new StackPanel { Spacing = 14, MaxWidth = 640, HorizontalAlignment = HorizontalAlignment.Left };
        panel.Children.Add(Eyebrow("WHERE IT LIVES"));
        panel.Children.Add(H1("A floating window, or docked to an edge?"));
        panel.Children.Add(Lede(
            "This is how the overlay sits on your screen. Floating is the easy default — you can switch any time from Settings."));

        var floating = PlaceCard(OverlayPresentationMode.Floating, "Floating",
            "A small panel that hovers over your desktop near a corner. Drag it wherever suits.", "Recommended", true);
        var docked = PlaceCard(OverlayPresentationMode.Docked, "Docked",
            _dockedSupported
                ? "Reserves a slim column at a screen edge, so your other windows never overlap it."
                : "Reserves a screen-edge column — available on Windows only.",
            _dockedSupported ? null : "Windows only", _dockedSupported);

        var row = new UniformGrid { Columns = 2, Margin = new Thickness(0, 4, 0, 0) };
        row.Children.Add(floating);
        row.Children.Add(docked);
        panel.Children.Add(row);
        return panel;
    }

    private Border PlaceCard(OverlayPresentationMode mode, string title, string blurb, string? tag, bool enabled)
    {
        var selected = _placement == mode;
        var body = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                PlacementArt(mode == OverlayPresentationMode.Docked),
                new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeight.SemiBold, Foreground = _text },
                new TextBlock { Text = blurb, FontSize = 12.5, Foreground = _muted, TextWrapping = TextWrapping.Wrap },
            },
        };
        if (tag != null)
            body.Children.Add(new Border
            {
                Background = enabled ? _onSoft : _button,
                CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 2),
                HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0),
                Child = new TextBlock { Text = tag, FontSize = 11, Foreground = enabled ? _on : _muted },
            });

        var card = new Border
        {
            Margin = new Thickness(6),
            Padding = new Thickness(16),
            CornerRadius = new CornerRadius(14),
            Background = selected ? _accentSoft : _surface,
            BorderBrush = selected ? _accent : _border,
            BorderThickness = new Thickness(selected ? 1.5 : 1),
            Opacity = enabled ? 1 : 0.5,
            Child = body,
        };
        if (enabled)
        {
            card.PointerPressed += (_, _) => { _placement = mode; GoTo(2); };
            card.Cursor = new Cursor(StandardCursorType.Hand);
        }
        return card;
    }

    // A small stand-in diagram of the overlay's placement: a "desktop" with either the panel floating over a
    // window's corner, or a reserved full-height column the windows sit beside. Deliberately minimal — it
    // shows the shape, not the overlay's contents.
    private Control PlacementArt(bool docked)
    {
        const double W = 250, H = 116;
        var canvas = new Canvas { Width = W, Height = H };

        if (docked)
        {
            canvas.Children.Add(MiniWindow(12, 14, W - 66, H - 28));   // windows stop before the column
            var col = new Border
            {
                Width = 34, Height = H - 12, CornerRadius = new CornerRadius(4),
                Background = _accentSoft, BorderBrush = _accent, BorderThickness = new Thickness(1.5),
            };
            Canvas.SetLeft(col, W - 42);
            Canvas.SetTop(col, 6);
            canvas.Children.Add(col);
        }
        else
        {
            canvas.Children.Add(MiniWindow(12, 14, W - 24, H - 28));   // a full window…
            var pnl = new Border                                       // …with the panel floating over its corner
            {
                Width = 52, Height = 70, CornerRadius = new CornerRadius(5),
                Background = _accentSoft, BorderBrush = _accent, BorderThickness = new Thickness(1.5),
            };
            Canvas.SetLeft(pnl, W - 70);
            Canvas.SetTop(pnl, 14);
            canvas.Children.Add(pnl);
        }

        return new Border
        {
            Width = W, Height = H, CornerRadius = new CornerRadius(9), Margin = new Thickness(0, 0, 0, 4),
            Background = _button, BorderBrush = _border, BorderThickness = new Thickness(1),
            ClipToBounds = true, Child = canvas,
        };
    }

    private Border MiniWindow(double x, double y, double w, double h)
    {
        var win = new Border
        {
            Width = w, Height = h, CornerRadius = new CornerRadius(4),
            Background = _surface, BorderBrush = _border, BorderThickness = new Thickness(1),
        };
        Canvas.SetLeft(win, x);
        Canvas.SetTop(win, y);
        return win;
    }

    // ── Step 3: Tier chooser + fine-tune ─────────────────────────────────────

    private Control BuildTierChooser()
    {
        _tierCards.Clear();
        var panel = new StackPanel { Spacing = 14, MaxWidth = 648, HorizontalAlignment = HorizontalAlignment.Left };
        panel.Children.Add(Eyebrow("CHOOSE YOUR SETUP"));
        panel.Children.Add(H1("How much Perch do you want, to start?"));
        panel.Children.Add(Lede(
            "Pick a starting point — then click any feature below to fine-tune it. Nothing's locked in; " +
            "you can change all of this later in Settings too."));

        var cards = new UniformGrid { Columns = 3 };
        foreach (var tier in OnboardingTiers.AllTiers)
            cards.Children.Add(TierCard(tier));
        panel.Children.Add(cards);

        _quietNote = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Margin = new Thickness(0, 2, 0, 0),
            Foreground = new SolidColorBrush(Palette.Yellow), IsVisible = false,
            Text = "🌙  Quiet mode is on right now, so the playful features you pick here will switch on when it ends.",
        };
        panel.Children.Add(_quietNote);

        // Breakdown header (lead + tally + reset).
        _breakdownLead = new TextBlock { FontSize = 15, FontWeight = FontWeight.SemiBold, Foreground = _text, VerticalAlignment = VerticalAlignment.Center };
        _tallyText = new TextBlock { FontSize = 12.5, Foreground = _muted, VerticalAlignment = VerticalAlignment.Center };
        _resetBtn = GhostButton("↺ Reset to tier");
        _resetBtn.Click += (_, _) => SelectPreset(_preset);

        var head = new DockPanel
        {
            LastChildFill = false, Margin = new Thickness(0, 4, 0, 2),
            Children =
            {
                WithDock(_breakdownLead, Dock.Left),
                WithDock(new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 10,
                    Children = { _resetBtn, _tallyText },
                }, Dock.Right),
            },
        };
        panel.Children.Add(head);
        panel.Children.Add(new Border { Height = 1, Background = _border, Margin = new Thickness(0, 0, 0, 4) });

        _breakdownHost = new StackPanel { Spacing = 12 };
        panel.Children.Add(_breakdownHost);

        panel.Children.Add(new Border
        {
            Margin = new Thickness(0, 6, 0, 0), Padding = new Thickness(14),
            CornerRadius = new CornerRadius(11), Background = _button,
            BorderBrush = _border, BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Foreground = _muted,
                Text = "🗄️  Whatever you leave off isn't gone — it's tucked away. Every feature stays one " +
                       "search away in Settings, and Quiet mode can hush the playful bits on demand. This screen " +
                       "only sets your starting point.",
            },
        });

        RenderBreakdown();
        return panel;
    }

    private Border TierCard(OnboardingTier tier)
    {
        var (glyph, blurb) = TierBlurb(tier);
        var card = new Border
        {
            Margin = new Thickness(6),
            Padding = new Thickness(16, 14),
            CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(1.5),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new StackPanel
            {
                Spacing = 3,
                Children =
                {
                    new TextBlock { Text = glyph, FontSize = 20 },
                    new TextBlock { Text = TierName(tier), FontSize = 18, FontWeight = FontWeight.SemiBold, Foreground = _text },
                    new TextBlock { Text = blurb, FontSize = 12, Foreground = _muted, TextWrapping = TextWrapping.Wrap, Height = 52 },
                    new TextBlock
                    {
                        Text = $"{OnboardingTiers.Count(tier)} features on", FontSize = 12,
                        Foreground = _muted, Margin = new Thickness(0, 6, 0, 0),
                    },
                },
            },
        };
        card.PointerPressed += (_, _) => SelectPreset(tier);
        _tierCards.Add((tier, card));
        return card;
    }

    private void SelectPreset(OnboardingTier tier)
    {
        _preset = tier;
        _enabled.Clear();
        foreach (var id in OnboardingTiers.Preset(tier)) _enabled.Add(id);
        RenderBreakdown();
    }

    private void ToggleItem(string id)
    {
        if (!_enabled.Remove(id)) _enabled.Add(id);
        RenderBreakdown();
    }

    private void RenderBreakdown()
    {
        if (_breakdownHost is null) return;

        _breakdownHost.Children.Clear();
        foreach (var group in OnboardingTiers.Groups)
        {
            var on = group.Items.Count(i => _enabled.Contains(i.Id));

            var headerRow = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 6) };
            headerRow.Children.Add(WithDock(new TextBlock
            {
                Text = $"{group.Icon}  {group.Name}", FontSize = 13.5, FontWeight = FontWeight.Bold, Foreground = _text,
            }, Dock.Left));
            headerRow.Children.Add(WithDock(new TextBlock
            {
                Text = $"{on}/{group.Items.Count}", FontSize = 11.5, Foreground = _muted, VerticalAlignment = VerticalAlignment.Center,
            }, Dock.Right));

            var chips = new WrapPanel { Orientation = Orientation.Horizontal };
            foreach (var item in group.Items)
                chips.Children.Add(Chip(item.Id));

            _breakdownHost.Children.Add(new StackPanel { Children = { headerRow, chips } });
        }

        UpdateTierSelection();
    }

    private Control Chip(string id)
    {
        var on = _enabled.Contains(id);
        var label = SettingsRegistry.ById(id)?.Name ?? id;
        var chip = new Border
        {
            Margin = new Thickness(0, 0, 6, 6),
            Padding = new Thickness(9, 4),
            CornerRadius = new CornerRadius(8),
            Background = on ? _onSoft : Brushes.Transparent,
            BorderBrush = on ? _on : _border,
            BorderThickness = new Thickness(1),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new TextBlock
            {
                Text = label, FontSize = 12, Foreground = on ? _on : _muted,
            },
        };
        chip.PointerPressed += (_, _) => ToggleItem(id);
        return chip;
    }

    private void UpdateTierSelection()
    {
        var matched = OnboardingTiers.MatchedTier(_enabled);
        foreach (var (tier, card) in _tierCards)
        {
            var sel = tier == matched;
            card.Background = sel ? _accentSoft : _surface;
            card.BorderBrush = sel ? _accent : _border;
        }

        if (_breakdownLead != null)
            _breakdownLead.Text = matched is { } t ? $"Here's what {TierName(t)} switches on" : "Here's your custom mix";
        if (_tallyText != null)
            _tallyText.Text = $"{_enabled.Count} of {OnboardingTiers.Managed.Count} features";
        if (_resetBtn != null)
            _resetBtn.IsVisible = matched is null;
        if (_quietNote != null)
            _quietNote.IsVisible = _quietActive && _enabled.Any(id => SettingsRegistry.ById(id)?.Playful == true);
        if (Current == StepKind.Tiers)
            _nextBtn.Content = NextLabel();
    }

    // ── Step 4: Done ─────────────────────────────────────────────────────────

    private Control BuildDone()
    {
        var matched = OnboardingTiers.MatchedTier(_enabled);
        var panel = new StackPanel { Spacing = 14, MaxWidth = 600, HorizontalAlignment = HorizontalAlignment.Left };
        panel.Children.Add(Eyebrow("YOU'RE ALL SET"));
        panel.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Center,
            Children = { H1("Perch is on its perch."), LogoMark(30) },
        });
        panel.Children.Add(Lede(
            "Your overlay will appear the next time a Claude Code session starts. Here's the setup you picked — nothing is locked in."));

        // Summary card.
        var body = new StackPanel { Spacing = 8, Margin = new Thickness(16) };
        body.Children.Add(SummaryLine("🪟 Overlay placement",
            _placement == OverlayPresentationMode.Floating ? "Floating" : "Docked"));
        foreach (var group in OnboardingTiers.Groups)
            body.Children.Add(SummaryLine($"{group.Icon} {group.Name}",
                $"{group.Items.Count(i => _enabled.Contains(i.Id))}/{group.Items.Count}"));

        panel.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(14), Background = _surface,
            BorderBrush = _border, BorderThickness = new Thickness(1),
            Child = new StackPanel
            {
                Children =
                {
                    new Border
                    {
                        Background = _accentSoft, Padding = new Thickness(16, 12),
                        BorderBrush = _border, BorderThickness = new Thickness(0, 0, 0, 1),
                        Child = new DockPanel
                        {
                            LastChildFill = false,
                            Children =
                            {
                                WithDock(new TextBlock
                                {
                                    Text = matched is { } t ? $"{TierBlurb(t).Glyph}  {TierName(t)}" : "🎚️  Custom setup",
                                    FontSize = 16, FontWeight = FontWeight.SemiBold, Foreground = _text,
                                }, Dock.Left),
                                WithDock(new TextBlock
                                {
                                    Text = $"{_enabled.Count} features on", FontSize = 12.5, Foreground = _muted,
                                    VerticalAlignment = VerticalAlignment.Center,
                                }, Dock.Right),
                            },
                        },
                    },
                    body,
                },
            },
        });

        panel.Children.Add(NextStep("🔎", "Everything's searchable.",
            "Settings has a search box — type \"chime\", \"Jira\", \"arcade\" and jump straight to any switch."));
        panel.Children.Add(NextStep("🎚️", "Reorder your overlay.",
            "Drag the sections in Settings' live preview to put what you care about on top."));
        panel.Children.Add(NextStep("↻", "Start over whenever.",
            "Re-run this quick start any time from Settings' Getting Started section — it won't undo later changes."));
        return panel;
    }

    // ── Small view helpers ───────────────────────────────────────────────────

    private TextBlock Eyebrow(string s) => new()
    {
        Text = s, FontSize = 11.5, FontWeight = FontWeight.SemiBold, Foreground = new SolidColorBrush(Palette.Accent),
    };

    private TextBlock H1(string s) => new()
    {
        Text = s, FontSize = 26, FontWeight = FontWeight.Bold, Foreground = _text, TextWrapping = TextWrapping.Wrap,
    };

    private TextBlock Lede(string s) => new()
    {
        Text = s, FontSize = 14.5, Foreground = _muted, TextWrapping = TextWrapping.Wrap,
    };

    private Control Fact(string glyph, string bold, string rest)
    {
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 14, MaxWidth = 560, VerticalAlignment = VerticalAlignment.Center };
        text.Inlines!.Add(new Run(bold + " ") { FontWeight = FontWeight.SemiBold, Foreground = _text });
        text.Inlines!.Add(new Run(rest) { Foreground = _muted });
        return new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 12,
            Children =
            {
                new Border
                {
                    Width = 32, Height = 32, CornerRadius = new CornerRadius(9), Background = _accentSoft,
                    VerticalAlignment = VerticalAlignment.Top,
                    Child = new TextBlock { Text = glyph, FontSize = 15, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                },
                text,
            },
        };
    }

    private Control TourRow(string n, string title, string blurb)
    {
        // DockPanel (not a horizontal StackPanel) so the text fills the real column width and wraps — a
        // StackPanel would measure the text at infinite width and let it spill under the preview column.
        var number = new Border
        {
            Width = 24, Height = 24, CornerRadius = new CornerRadius(7), Background = _accent,
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 13, 0),
            [DockPanel.DockProperty] = Dock.Left,
            Child = new TextBlock
            {
                Text = n, FontSize = 12, FontWeight = FontWeight.Bold, Foreground = _onAccent,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
        var text = new StackPanel
        {
            Children =
            {
                new TextBlock { Text = title, FontSize = 14.5, FontWeight = FontWeight.SemiBold, Foreground = _text, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = blurb, FontSize = 13, Foreground = _muted, TextWrapping = TextWrapping.Wrap },
            },
        };
        return new DockPanel { LastChildFill = true, Children = { number, text } };
    }

    private Control SummaryLine(string k, string v)
    {
        return new DockPanel
        {
            LastChildFill = false,
            Children =
            {
                WithDock(new TextBlock { Text = k, FontSize = 13, Foreground = _muted }, Dock.Left),
                WithDock(new TextBlock { Text = v, FontSize = 12.5, Foreground = _text }, Dock.Right),
            },
        };
    }

    private Control NextStep(string glyph, string bold, string rest)
    {
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 14, MaxWidth = 560 };
        text.Inlines!.Add(new Run(bold + " ") { FontWeight = FontWeight.SemiBold, Foreground = _text });
        text.Inlines!.Add(new Run(rest) { Foreground = _muted });
        return new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 11,
            Children =
            {
                new TextBlock { Text = glyph, FontSize = 16, VerticalAlignment = VerticalAlignment.Top },
                text,
            },
        };
    }

    private Button GhostButton(string text) => new()
    {
        Content = text, FontSize = 12.5, Padding = new Thickness(12, 7), CornerRadius = new CornerRadius(8),
        Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = _muted,
    };

    private void StylePrimary(Button b)
    {
        b.MinWidth = 128;
        b.Padding = new Thickness(18, 9);
        b.CornerRadius = new CornerRadius(9);
        b.Background = _accent;
        b.Foreground = _onAccent;
        b.FontWeight = FontWeight.SemiBold;
        b.HorizontalContentAlignment = HorizontalAlignment.Center;
    }

    private static T WithDock<T>(T control, Dock dock) where T : Control
    {
        control[DockPanel.DockProperty] = dock;
        return control;
    }

    private static string TierName(OnboardingTier t) => t switch
    {
        OnboardingTier.Basic => "Basic",
        OnboardingTier.Intermediate => "Intermediate",
        _ => "Kitchen sink",
    };

    private static (string Glyph, string Blurb) TierBlurb(OnboardingTier t) => t switch
    {
        OnboardingTier.Basic => ("🌱", "The calm essentials. Status at a glance, and a nudge when a run's done."),
        OnboardingTier.Intermediate => ("⚙️", "Basic, plus the productivity kit — PRs, tickets, dashboards, todos, a little delight."),
        _ => ("🎡", "The lot. Social, games, phone push — every switch flipped on."),
    };
}

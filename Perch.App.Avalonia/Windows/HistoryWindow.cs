using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Perch.Avalonia.Rendering;
using Perch.Avalonia.Theming;
using Perch.Data;

namespace Perch.Avalonia.Windows;

/// <summary>
/// The session-history viewer (the Avalonia port of <c>HistoryViewerForm</c>). A toolbar with a session
/// dropdown (every transcript across all projects, active first then newest-first), a Readable/Raw
/// toggle, and a Follow button sits over a scrolled transcript body. Readable view renders each event as
/// its own block — Markdown-formatted prose (<see cref="MarkdownRender"/>) and collapsible
/// <see cref="Expander"/>s for tool calls; Raw view is the verbatim monospace timeline. Active sessions
/// are tailed live (<see cref="FileSystemWatcher"/>); Follow keeps the view pinned to the newest event.
/// Large transcripts are gated behind an explicit confirmation.
/// </summary>
internal sealed class HistoryWindow : Window
{
    private static readonly Color BodyBg = Color.FromRgb(18, 18, 24);
    private static readonly IBrush UserBrush   = new SolidColorBrush(Palette.Green);
    private static readonly IBrush AsstBrush   = new SolidColorBrush(Palette.Accent);
    private static readonly IBrush ToolBrush   = new SolidColorBrush(Color.FromRgb(56, 189, 248));
    private static readonly IBrush MutedBrush  = new SolidColorBrush(Palette.Muted);
    private static readonly IBrush FgBrush     = new SolidColorBrush(Palette.Fg);
    private static readonly IBrush TitleBrush  = new SolidColorBrush(Palette.Title);
    private static readonly FontFamily Mono    = new("Cascadia Code, Consolas, Menlo, monospace");

    private readonly ComboBox _dropdown;
    private readonly Button _readableBtn;
    private readonly Button _rawBtn;
    private readonly Button _followBtn;
    private readonly ScrollViewer _scroll;

    private HashSet<string> _activeIds = new();
    private List<HistoryEntry> _entries = new();
    private bool _listed;
    private bool _suppressSelect;
    private string? _pendingSelect;
    private bool _pendingSelectSet;

    private TranscriptParser? _parser;
    private HistoryEntry? _loaded;
    private bool _raw;
    private bool _follow;
    private readonly HashSet<string> _expanded = new();

    private readonly FileSystemWatcher _watcher = new();
    private DispatcherTimer? _tailDebounce;

    public HistoryWindow()
    {
        Title = "Session history";
        Width = 900;
        Height = 640;
        MinWidth = 560;
        MinHeight = 400;
        Background = new SolidColorBrush(BodyBg);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _dropdown = new ComboBox
        {
            MinWidth = 340, MaxDropDownHeight = 480, VerticalAlignment = VerticalAlignment.Center,
            ItemTemplate = new FuncDataTemplate<HistoryEntry>((e, _) => new TextBlock
            {
                Text = DisplayName(e), Foreground = e is { IsActive: true } ? AsstBrush : FgBrush,
            }, supportsRecycling: true),
        };
        _dropdown.SelectionChanged += (_, _) =>
        {
            if (_suppressSelect || _dropdown.SelectedItem is not HistoryEntry entry) return;
            LoadTranscript(entry);
        };

        _readableBtn = ToggleButton("Readable", () => SetRaw(false));
        _rawBtn = ToggleButton("Raw", () => SetRaw(true));
        _followBtn = ToggleButton("Follow", ToggleFollow);

        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(12, 9), Height = 46,
            Children = { _dropdown, _readableBtn, _rawBtn, _followBtn },
        };
        var toolbarPanel = new Border { Background = Palette.FormBgBrush, Child = toolbar, [DockPanel.DockProperty] = Dock.Top };

        _scroll = new ScrollViewer { Padding = new Thickness(0) };
        Content = new DockPanel { Children = { toolbarPanel, _scroll } };

        _watcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size;
        _watcher.Changed += OnTranscriptChanged;
        _watcher.Created += OnTranscriptChanged;

        Closed += (_, _) => { try { _watcher.EnableRaisingEvents = false; } catch { } _watcher.Dispose(); };

        SetPlaceholderBody("Select a session to read its transcript.");
        UpdateToggleButtons();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        LoadList();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        base.OnKeyDown(e);
    }

    public void SetActiveSessions(IReadOnlyList<ClaudeSession> sessions)
    {
        var ids = sessions.Select(s => s.SessionId).ToHashSet();
        if (ids.SetEquals(_activeIds)) return;
        _activeIds = ids;
        if (_listed) LoadList();
    }

    public void ShowSession(string? sessionId)
    {
        _pendingSelect = sessionId;
        _pendingSelectSet = true;
        if (_listed) ApplyPendingSelect();
    }

    // ── List loading ────────────────────────────────────────────────────────────
    private void LoadList()
    {
        var active = _activeIds;
        System.Threading.Tasks.Task.Run(() => SessionHistory.ListAll(active)).ContinueWith(t =>
        {
            if (!t.IsCompletedSuccessfully) return;
            Dispatcher.UIThread.Post(() =>
            {
                if (!IsVisible) return;
                _entries = t.Result;
                _entries.Insert(0, HistoryEntry.Placeholder);

                var keepId = (_dropdown.SelectedItem as HistoryEntry)?.SessionId ?? _loaded?.SessionId;
                _suppressSelect = true;
                _dropdown.ItemsSource = _entries;
                _dropdown.SelectedItem = _entries.FirstOrDefault(e => e.SessionId == keepId) ?? _entries[0];
                _suppressSelect = false;
                _listed = true;

                ApplyPendingSelect();
            });
        });
    }

    private void ApplyPendingSelect()
    {
        if (!_pendingSelectSet) return;
        _pendingSelectSet = false;
        if (string.IsNullOrEmpty(_pendingSelect)) return;
        var entry = _entries.FirstOrDefault(e => e.SessionId == _pendingSelect);
        if (entry is not null) _dropdown.SelectedItem = entry;
    }

    // ── Transcript loading ────────────────────────────────────────────────────────
    private void LoadTranscript(HistoryEntry entry)
    {
        StopWatching();
        _parser = null;
        _expanded.Clear();

        if (entry.IsPlaceholder)
        {
            _loaded = null;
            SetPlaceholderBody("Select a session to read its transcript.");
            return;
        }

        _loaded = entry;
        _follow = entry.IsActive;
        UpdateToggleButtons();

        // Large transcripts can lag or exhaust memory — gate behind an explicit confirmation.
        if (entry.IsLarge)
        {
            var confirm = new Button { Content = $"Load anyway ({entry.SizeLabel})", Margin = new Thickness(0, 12, 0, 0) };
            confirm.Click += (_, _) => ParseAndRender(entry);
            _scroll.Content = new StackPanel
            {
                Margin = new Thickness(20),
                Children =
                {
                    new TextBlock { Text = "This transcript is large and may be slow to render.", Foreground = MutedBrush },
                    confirm,
                },
            };
            return;
        }

        SetPlaceholderBody("Loading…");
        ParseAndRender(entry);
    }

    private void ParseAndRender(HistoryEntry entry)
    {
        SetPlaceholderBody("Loading…");
        var path = entry.Path;
        System.Threading.Tasks.Task.Run(() =>
        {
            var parser = new TranscriptParser(path);
            parser.Ingest();
            return parser;
        }).ContinueWith(t =>
        {
            if (!t.IsCompletedSuccessfully) return;
            Dispatcher.UIThread.Post(() =>
            {
                if (!IsVisible || _loaded?.SessionId != entry.SessionId) return;
                _parser = t.Result;
                Render();
                if (_follow) _scroll.ScrollToEnd();
                if (entry.IsActive) StartWatching(path);
            });
        });
    }

    // ── Live tail ────────────────────────────────────────────────────────────────
    private void StartWatching(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(dir)) return;
            _watcher.EnableRaisingEvents = false;
            _watcher.Path = dir;
            _watcher.Filter = Path.GetFileName(path);
            _watcher.EnableRaisingEvents = true;
        }
        catch { /* best-effort tailing */ }
    }

    private void StopWatching()
    {
        try { _watcher.EnableRaisingEvents = false; } catch { }
    }

    // FileSystemWatcher fires on a background thread and can burst; debounce onto the UI thread.
    private void OnTranscriptChanged(object? sender, FileSystemEventArgs e) =>
        Dispatcher.UIThread.Post(() =>
        {
            _tailDebounce ??= CreateTailDebounce();
            _tailDebounce.Stop();
            _tailDebounce.Start();
        });

    private DispatcherTimer CreateTailDebounce()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            if (_parser is null) return;
            if (_parser.Ingest().HasNew)
            {
                Render();
                if (_follow) _scroll.ScrollToEnd();
            }
        };
        return t;
    }

    // ── View toggles ────────────────────────────────────────────────────────────
    private void SetRaw(bool raw)
    {
        if (_raw == raw) return;
        _raw = raw;
        UpdateToggleButtons();
        Render();
    }

    private void ToggleFollow()
    {
        _follow = !_follow;
        UpdateToggleButtons();
        if (_follow) _scroll.ScrollToEnd();
    }

    // ── Rendering ────────────────────────────────────────────────────────────────
    private void Render()
    {
        var events = _parser?.Events ?? [];
        if (events.Count == 0) { SetPlaceholderBody("This transcript has no readable events yet."); return; }

        _scroll.Content = _raw ? RenderRaw(events) : RenderReadable(events);
    }

    private Control RenderRaw(IReadOnlyList<HistoryEvent> events)
    {
        var body = new SelectableTextBlock { Margin = new Thickness(16), FontFamily = Mono, FontSize = 12.5, Foreground = FgBrush };
        var inlines = new InlineCollection();
        foreach (var ev in events)
        {
            string ts = ev.Timestamp is { } t ? t.ToLocalTime().ToString("HH:mm:ss") : "        ";
            inlines.Add(new Run($"[{ts}] {ev.Kind}  ") { Foreground = MutedBrush });
            inlines.Add(new Run(OneLine(ev.Summary) + "\n") { Foreground = FgBrush });
            if (ev.Detail.Length > 0) inlines.Add(new Run(ev.Detail + "\n") { Foreground = FgBrush });
            if (!string.IsNullOrEmpty(ev.Result)) inlines.Add(new Run("→ " + ev.Result + "\n") { Foreground = MutedBrush });
            inlines.Add(new Run("\n"));
        }
        body.Inlines = inlines;
        return body;
    }

    private Control RenderReadable(IReadOnlyList<HistoryEvent> events)
    {
        var panel = new StackPanel { Margin = new Thickness(16), Spacing = 8 };
        foreach (var ev in events)
        {
            if (ev.Kind == HistoryEventKind.Meta) continue;
            panel.Children.Add(ev.Kind switch
            {
                HistoryEventKind.ToolCall => ToolBlock(ev),
                HistoryEventKind.Image    => ImageBlock(ev),
                _                         => ProseBlock(ev),
            });
        }
        return panel;
    }

    private Control ProseBlock(HistoryEvent ev)
    {
        var (label, brush) = ev.Kind switch
        {
            HistoryEventKind.UserText      => ("You", UserBrush),
            HistoryEventKind.AssistantText => (ev.IsSidechain ? "Sub-agent" : "Claude", AsstBrush),
            HistoryEventKind.Thinking      => ("thinking", MutedBrush),
            _                              => (ev.Kind.ToString(), MutedBrush),
        };

        var header = new SelectableTextBlock { FontWeight = FontWeight.Bold, Foreground = brush, FontSize = 13 };
        header.Inlines = new InlineCollection { new Run(label) };

        var body = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, Foreground = FgBrush, FontSize = 13, Margin = new Thickness(0, 2, 0, 0) };
        var inlines = new InlineCollection();
        if (ev.Kind == HistoryEventKind.Thinking)
        {
            body.FontStyle = FontStyle.Italic;
            body.Foreground = MutedBrush;
            inlines.Add(new Run(ev.Detail));
        }
        else
        {
            MarkdownRender.Append(inlines, ev.Detail.Length > 0 ? ev.Detail : ev.Summary,
                FgBrush, MutedBrush, ToolBrush, AsstBrush, TitleBrush);
        }
        body.Inlines = inlines;

        return new StackPanel { Margin = new Thickness(ev.IsSidechain ? 24 : 0, 0, 0, 0), Children = { header, body } };
    }

    private Control ToolBlock(HistoryEvent ev)
    {
        var content = new SelectableTextBlock { FontFamily = Mono, FontSize = 12, Foreground = MutedBrush, TextWrapping = TextWrapping.Wrap };
        var inlines = new InlineCollection();
        if (!string.IsNullOrWhiteSpace(ev.Detail))
        {
            inlines.Add(new Run("input:\n") { Foreground = MutedBrush });
            inlines.Add(new Run(ev.Detail + "\n\n") { Foreground = FgBrush });
        }
        if (!string.IsNullOrWhiteSpace(ev.Result))
        {
            inlines.Add(new Run("result:\n") { Foreground = MutedBrush });
            inlines.Add(new Run(ClipText(ev.Result!, 4000)) { Foreground = FgBrush });
        }
        content.Inlines = inlines;

        var expander = new Expander
        {
            Header = "⚙ " + OneLine(ev.Summary),
            Foreground = ToolBrush,
            IsExpanded = _expanded.Contains(ev.Key),
            Content = new Border { Padding = new Thickness(8, 4), Child = content },
        };
        expander.PropertyChanged += (_, args) =>
        {
            if (args.Property == Expander.IsExpandedProperty)
            {
                if (expander.IsExpanded) _expanded.Add(ev.Key); else _expanded.Remove(ev.Key);
            }
        };
        return expander;
    }

    private Control ImageBlock(HistoryEvent ev)
    {
        var btn = new Button
        {
            Content = $"🖼 View image ({ev.ImageMedia ?? "image"})",
            Foreground = AsstBrush, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Padding = new Thickness(0), Cursor = new Cursor(StandardCursorType.Hand),
        };
        btn.Click += (_, _) =>
        {
            if (!string.IsNullOrEmpty(ev.ImageUrl)) OpenUrl(ev.ImageUrl!);
            else if (!string.IsNullOrEmpty(ev.ImageData)) OpenImageData(ev.ImageData!, ev.ImageMedia ?? "image/png");
        };
        return btn;
    }

    private void SetPlaceholderBody(string text) =>
        _scroll.Content = new TextBlock { Margin = new Thickness(20), Foreground = MutedBrush, Text = text };

    // ── Toolbar helpers ────────────────────────────────────────────────────────
    private Button ToggleButton(string text, Action onClick)
    {
        var b = new Button
        {
            Content = text, Height = 28, FontSize = 12, CornerRadius = new CornerRadius(6),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
        };
        b.Click += (_, _) => onClick();
        return b;
    }

    private void UpdateToggleButtons()
    {
        Style(_readableBtn, !_raw);
        Style(_rawBtn, _raw);
        Style(_followBtn, _follow);
        _followBtn.Content = _follow ? "Following" : "Follow";

        static void Style(Button b, bool active)
        {
            b.Background = active ? new SolidColorBrush(Palette.Accent) : new SolidColorBrush(Palette.ButtonBg);
            b.Foreground = active ? Brushes.White : Palette.FgBrush;
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────
    private static string OneLine(string s) { int nl = s.IndexOf('\n'); return nl >= 0 ? s[..nl] : s; }
    private static string ClipText(string s, int max) => s.Length > max ? s[..max].TrimEnd() + "\n… (truncated)" : s;

    private static void OpenUrl(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* best-effort */ }
    }

    // Decodes an inline base64 image to a temp file and opens it in the default viewer.
    private static void OpenImageData(string base64, string media)
    {
        try
        {
            string ext = media.Contains('/') ? media[(media.IndexOf('/') + 1)..] : "png";
            var bytes = Convert.FromBase64String(base64);
            string file = Path.Combine(Path.GetTempPath(), $"perch-image-{Guid.NewGuid():N}.{ext}");
            File.WriteAllBytes(file, bytes);
            OpenUrl(file);
        }
        catch { /* malformed data — nothing useful to do */ }
    }

    private static string DisplayName(HistoryEntry e)
    {
        if (e.IsPlaceholder) return e.ProjectName;
        string size = e.SizeBytes > 0 ? $" · {e.SizeLabel}" : "";
        string active = e.IsActive ? "● " : "";
        return $"{active}{e.ProjectName} · {e.RelativeTime}{size}";
    }
}

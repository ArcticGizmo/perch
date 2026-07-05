using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Perch.Avalonia.Theming;
using Perch.Data;

namespace Perch.Avalonia.Windows;

/// <summary>
/// The session-history viewer (the Avalonia port of <c>HistoryViewerForm</c>). A toolbar with a session
/// dropdown (every transcript across all projects, active first then newest-first) and a Readable/Raw
/// toggle sits over a scrolled, selectable transcript body. Selecting a session parses it via
/// <see cref="TranscriptParser"/> off the UI thread.
///
/// Step 5.7a lands the chrome, the dropdown + selection, off-thread list/transcript loading, and the
/// readable/raw text rendering. Rich markdown, clickable tool-summary expand/collapse, image links, and
/// live tailing (Follow) follow in 5.7b.
/// </summary>
internal sealed class HistoryWindow : Window
{
    private static readonly Color BodyBg = Color.FromRgb(18, 18, 24);
    private static readonly IBrush UserBrush   = new SolidColorBrush(Palette.Accent);
    private static readonly IBrush AsstBrush   = new SolidColorBrush(Palette.Title);
    private static readonly IBrush ToolBrush   = new SolidColorBrush(Color.FromRgb(56, 189, 248));
    private static readonly IBrush MutedBrush  = new SolidColorBrush(Palette.Muted);
    private static readonly IBrush FgBrush     = new SolidColorBrush(Palette.Fg);

    private readonly ComboBox _dropdown;
    private readonly Button _readableBtn;
    private readonly Button _rawBtn;
    private readonly ScrollViewer _scroll;
    private readonly SelectableTextBlock _body;

    private HashSet<string> _activeIds = new();
    private List<HistoryEntry> _entries = new();
    private bool _listed;
    private bool _suppressSelect;
    private string? _pendingSelect;
    private bool _pendingSelectSet;

    private TranscriptParser? _parser;
    private string? _loadedSessionId;
    private bool _raw;

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
            MinWidth = 360, MaxDropDownHeight = 480, VerticalAlignment = VerticalAlignment.Center,
            ItemTemplate = new FuncDataTemplate<HistoryEntry>((e, _) => new TextBlock
            {
                Text = DisplayName(e),
                Foreground = e is { IsActive: true } ? UserBrush : FgBrush,
            }, supportsRecycling: true),
        };
        _dropdown.SelectionChanged += (_, _) =>
        {
            if (_suppressSelect || _dropdown.SelectedItem is not HistoryEntry entry) return;
            LoadTranscript(entry);
        };

        _readableBtn = ToggleButton("Readable", () => SetRaw(false));
        _rawBtn = ToggleButton("Raw", () => SetRaw(true));

        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(12, 9), Height = 46,
            Children = { _dropdown, _readableBtn, _rawBtn },
        };
        var toolbarPanel = new Border { Background = Palette.FormBgBrush, Child = toolbar, [DockPanel.DockProperty] = Dock.Top };

        _body = new SelectableTextBlock
        {
            Margin = new Thickness(16), TextWrapping = TextWrapping.Wrap, Foreground = FgBrush, FontSize = 13,
        };
        _scroll = new ScrollViewer { Content = _body, Padding = new Thickness(0) };

        Content = new DockPanel { Children = { toolbarPanel, _scroll } };
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

    /// <summary>Feeds the current live sessions so the dropdown can mark/sort active ones. Reloads the
    /// list when the active set changes so a newly-active session floats to the top.</summary>
    public void SetActiveSessions(IReadOnlyList<ClaudeSession> sessions)
    {
        var ids = sessions.Select(s => s.SessionId).ToHashSet();
        if (ids.SetEquals(_activeIds)) return;
        _activeIds = ids;
        if (_listed) LoadList();
    }

    /// <summary>Opens the viewer on a specific session (overlay "View history" / plugin jump). Remembers
    /// the request if the list hasn't loaded yet, applying it once it has.</summary>
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

                var keepId = (_dropdown.SelectedItem as HistoryEntry)?.SessionId ?? _loadedSessionId;
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
        var id = _pendingSelect;
        if (string.IsNullOrEmpty(id)) return;
        var entry = _entries.FirstOrDefault(e => e.SessionId == id);
        if (entry is not null) _dropdown.SelectedItem = entry; // fires SelectionChanged → load
    }

    // ── Transcript loading ────────────────────────────────────────────────────────
    private void LoadTranscript(HistoryEntry entry)
    {
        if (entry.IsPlaceholder)
        {
            _parser = null;
            _loadedSessionId = null;
            _body.Inlines?.Clear();
            _body.Text = "Select a session to read its transcript.";
            return;
        }

        _loadedSessionId = entry.SessionId;
        _body.Inlines?.Clear();
        _body.Text = "Loading…";

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
                if (!IsVisible || _loadedSessionId != entry.SessionId) return; // a newer selection won
                _parser = t.Result;
                Render();
            });
        });
    }

    private void SetRaw(bool raw)
    {
        if (_raw == raw) return;
        _raw = raw;
        UpdateToggleButtons();
        Render();
    }

    // ── Rendering (5.7a: readable prose + raw timeline as selectable text; rich markdown/expand in 5.7b) ──
    private void Render()
    {
        _body.Text = null;
        var inlines = new InlineCollection();
        var events = _parser?.Events ?? [];

        if (events.Count == 0)
        {
            _body.Text = "This transcript has no readable events yet.";
            return;
        }

        if (_raw)
        {
            _body.FontFamily = new FontFamily("Cascadia Code, Consolas, Menlo, monospace");
            foreach (var ev in events)
            {
                string ts = ev.Timestamp is { } t ? t.ToLocalTime().ToString("HH:mm:ss") : "        ";
                inlines.Add(new Run($"[{ts}] {ev.Kind}  ") { Foreground = MutedBrush });
                inlines.Add(new Run(OneLine(ev.Summary) + "\n") { Foreground = FgBrush });
                if (ev.Detail.Length > 0) inlines.Add(new Run(ev.Detail + "\n") { Foreground = FgBrush });
                if (!string.IsNullOrEmpty(ev.Result)) inlines.Add(new Run("→ " + ev.Result + "\n") { Foreground = MutedBrush });
                inlines.Add(new Run("\n"));
            }
        }
        else
        {
            _body.FontFamily = FontFamily.Default;
            foreach (var ev in events)
            {
                if (ev.Kind == HistoryEventKind.Meta) continue;
                var (label, brush) = RoleLabel(ev);
                inlines.Add(new Run(label + "\n") { Foreground = brush, FontWeight = FontWeight.Bold });
                string text = ev.Kind == HistoryEventKind.ToolCall
                    ? OneLine(ev.Summary) + (string.IsNullOrEmpty(ev.Result) ? "" : "\n" + Trim(ev.Result))
                    : (ev.Detail.Length > 0 ? ev.Detail : ev.Summary);
                if (ev.Kind == HistoryEventKind.Thinking) inlines.Add(new Run(text + "\n\n") { Foreground = MutedBrush, FontStyle = FontStyle.Italic });
                else inlines.Add(new Run(text + "\n\n") { Foreground = FgBrush });
            }
        }

        _body.Inlines = inlines;
        _scroll.Offset = new Vector(0, 0);
    }

    private static (string, IBrush) RoleLabel(HistoryEvent ev) => ev.Kind switch
    {
        HistoryEventKind.UserText      => ("You", UserBrush),
        HistoryEventKind.AssistantText => ("Claude", AsstBrush),
        HistoryEventKind.Thinking      => ("Thinking", MutedBrush),
        HistoryEventKind.ToolCall      => ("⚙ " + OneLine(ev.Summary), ToolBrush),
        HistoryEventKind.Image         => ("Image", ToolBrush),
        _                              => (ev.Kind.ToString(), MutedBrush),
    };

    private static string OneLine(string s)
    {
        int nl = s.IndexOf('\n');
        return nl >= 0 ? s[..nl] : s;
    }

    private static string Trim(string s) => s.Length > 4000 ? s[..4000].TrimEnd() + "\n… (truncated)" : s;

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

        static void Style(Button b, bool active)
        {
            b.Background = active ? new SolidColorBrush(Palette.Accent) : new SolidColorBrush(Palette.ButtonBg);
            b.Foreground = active ? Brushes.White : Palette.FgBrush;
        }
    }

    private static string DisplayName(HistoryEntry e)
    {
        if (e.IsPlaceholder) return e.ProjectName;
        string size = e.SizeBytes > 0 ? $" · {e.SizeLabel}" : "";
        string active = e.IsActive ? "● " : "";
        return $"{active}{e.ProjectName} · {e.RelativeTime}{size}";
    }
}

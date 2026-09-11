using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Perch.Avalonia.Theming;
using Perch.Avalonia.Views;
using Perch.Data;
using Perch.Data.Control;

namespace Perch.Avalonia.Windows;

/// <summary>
/// The session-history viewer: a <em>read-only</em> render of a transcript in the rich session UI
/// (<see cref="SessionThreadView"/>) — the same bubbles, tool cards, diffs and Markdown a live session shows,
/// with no composer or controls. The transcript on disk is decoded into a <see cref="SessionConversation"/>
/// (<see cref="SessionConversation.AppendTranscriptLine"/>) and bound to the thread. Active sessions are
/// tailed live (<see cref="FileSystemWatcher"/>): the newly-appended transcript lines are folded in
/// incrementally, so the view grows without a rebuild and keeps its scroll position and expanded tool cards.
/// Large transcripts are gated behind an explicit confirmation. File references in the render (tool-card
/// paths, inline-code paths) route out to the app-owned Markdown viewer / diff tree.
///
/// Picking a session is a separate command-palette modal (<see cref="HistorySearchWindow"/>), opened from the
/// toolbar's "Search sessions" button — the same idiom as the global Alt+Shift+Space switcher. The toolbar
/// itself is a session header showing the currently-open session (brand mark, project/title name, cwd, and a
/// click-to-copy id), so the viewer reads like the live session window.
/// </summary>
internal sealed class HistoryWindow : Window
{
    private const double HeaderHeight = 68;   // toolbar band height — fits the session header (name / path / id)

    private readonly SessionPalette _p = SessionPalette.Current;

    // ── Toolbar: session header (selected session) + a Search button ──────────────
    private readonly TextBlock _projectText;  // project/title name of the open session
    private readonly TextBlock _pathText;     // its cwd (tail kept)
    private readonly Border _searchButton;
    private HistoryEntry? _selected;

    // ── Header session-id line (click to copy the full id for debugging) ──────────
    private readonly Border _idChip;
    private readonly TextBlock _idChipText;

    // ── Floating navigation (top / prev-prompt / next-prompt / bottom) ────────────
    private readonly Border _jumpTopBtn, _jumpPrevBtn, _jumpNextBtn, _jumpBottomBtn;

    private readonly Border _bodyHost;
    private readonly SessionThreadView _thread;

    private HashSet<string> _activeIds = new();
    private List<HistoryEntry> _entries = new();
    private bool _listed;
    private string? _pendingSelect;
    private bool _pendingSelectSet;

    private HistoryEntry? _loaded;
    private SessionConversation? _conv;
    private int _consumedLines;      // transcript lines already folded into _conv (for incremental tailing)
    private bool _tailBusy;          // a tail re-read is in flight — coalesce further ticks
    private bool _renderSample;

    private readonly FileSystemWatcher _watcher = new();
    private DispatcherTimer? _tailDebounce;

    /// <summary>A file reference in the render was opened (a tool-card path or an inline-code path):
    /// (cwd, sessionId, isActive, absolute path). Routed out because the app owns the Markdown viewer.</summary>
    public event Action<string, string, bool, string>? OpenFileInViewerRequested;

    /// <summary>A file reference's "View diff" was picked: (cwd, isActive, absolute path). Opens the git tree.</summary>
    public event Action<string, bool, string>? ViewFileDiffRequested;

    public HistoryWindow()
    {
        Title = "Session history";
        Width = 900;
        Height = 640;
        MinWidth = 560;
        MinHeight = 400;
        Background = _p.Surface;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        // ── Session header: the currently-open session, reading like the live session window's crumb ──
        var markBadge = new Border
        {
            Width = 28, Height = 28, CornerRadius = new CornerRadius(8), Background = _p.BrandWash,
            BorderBrush = _p.BrandLine, BorderThickness = new Thickness(1), Child = SessionThreadView.MarkImage(18),
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 12, 0), [DockPanel.DockProperty] = Dock.Left,
        };
        _projectText = new TextBlock
        {
            FontFamily = _p.Display, FontWeight = FontWeight.Bold, FontSize = 15, Foreground = _p.Title,
            Text = "No session selected", TextTrimming = TextTrimming.CharacterEllipsis,
        };
        // A path keeps its tail (the folder name) and elides the head — the file-path truncation convention.
        _pathText = new TextBlock
        {
            FontFamily = _p.Mono, FontSize = 12, Foreground = _p.Faint, Text = "Search sessions to open one",
            TextTrimming = TextTrimming.PrefixCharacterEllipsis, Margin = new Thickness(0, 1, 0, 0),
        };
        // The selected session's id, click-to-copy for debugging (shortened, full id copied + tip).
        _idChipText = new TextBlock { FontFamily = _p.Mono, FontSize = 11, Foreground = _p.Faint, Opacity = 0.8, VerticalAlignment = VerticalAlignment.Center };
        _idChip = new Border
        {
            CornerRadius = new CornerRadius(4), Padding = new Thickness(0, 1), Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand), HorizontalAlignment = HorizontalAlignment.Left,
            IsVisible = false, Child = _idChipText, Margin = new Thickness(0, 1, 0, 0),
            [ToolTip.TipProperty] = "Copy session id",
        };
        _idChip.PointerEntered += (_, _) => _idChipText.Opacity = 1;
        _idChip.PointerExited += (_, _) => _idChipText.Opacity = 0.8;
        _idChip.PointerReleased += (_, e) => { if (e.InitialPressMouseButton == MouseButton.Left) CopySessionId(); };

        var crumb = new DockPanel
        {
            LastChildFill = true, VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                markBadge,
                new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { _projectText, _pathText, _idChip } },
            },
        };

        // ── Search button: opens the command-palette modal ──
        var searchGlyph = new global::Avalonia.Controls.Shapes.Path
        {
            Stroke = _p.Muted, StrokeThickness = 1.5, Width = 14, Height = 14,
            Data = Geometry.Parse("M2,6 a4,4 0 1 0 8,0 a4,4 0 1 0 -8,0 M9.2,9.2 L13,13"),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0),
        };
        _searchButton = new Border
        {
            Background = _p.Raised2, BorderBrush = _p.Border, BorderThickness = new Thickness(1),
            CornerRadius = SessionPalette.ButtonRadius, Padding = new Thickness(13, 0), Height = 36,
            Cursor = new Cursor(StandardCursorType.Hand), VerticalAlignment = VerticalAlignment.Center,
            [DockPanel.DockProperty] = Dock.Right,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    searchGlyph,
                    new TextBlock { Text = "Search sessions", FontFamily = _p.Body, FontSize = 13.5, FontWeight = FontWeight.SemiBold, Foreground = _p.Muted, VerticalAlignment = VerticalAlignment.Center },
                },
            },
        };
        _searchButton.PointerEntered += (_, _) => _searchButton.Background = _p.Raised;
        _searchButton.PointerExited += (_, _) => _searchButton.Background = _p.Raised2;
        _searchButton.PointerReleased += (_, e) => { if (e.InitialPressMouseButton == MouseButton.Left) OpenSearch(); };

        var toolbarContent = new DockPanel
        {
            LastChildFill = true, Margin = new Thickness(14, 0), VerticalAlignment = VerticalAlignment.Center,
            Children = { _searchButton, crumb },   // search docks right, header fills
        };
        var toolbar = new Border
        {
            Height = HeaderHeight, Background = _p.Raised, BorderBrush = _p.Border,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = toolbarContent, [DockPanel.DockProperty] = Dock.Top,
        };

        _thread = new SessionThreadView(_p);
        _thread.OpenFileRequested += path =>
            OpenFileInViewerRequested?.Invoke(_loaded?.Cwd ?? "", _loaded?.SessionId ?? "", _loaded?.IsActive ?? false, path);
        _thread.ViewDiffRequested += path =>
            ViewFileDiffRequested?.Invoke(_loaded?.Cwd ?? "", _loaded?.IsActive ?? false, path);

        _bodyHost = new Border();
        var mainDock = new DockPanel { Children = { toolbar, _bodyHost } };

        // Floating navigation over the thread, bottom-right — top / previous prompt / next prompt / bottom,
        // each shown only when it would do something (mirrors the live session's jump buttons + top/bottom).
        _jumpTopBtn = JumpButton("⤒", "Jump to the top", () => _thread.JumpToTop());
        _jumpPrevBtn = JumpButton("↑", "Jump to the previous prompt", () => _thread.JumpToPreviousPrompt());
        _jumpNextBtn = JumpButton("↓", "Jump to the next prompt", () => _thread.JumpToNextPrompt());
        _jumpBottomBtn = JumpButton("⤓", "Jump to the bottom", () => _thread.JumpToBottom());
        var jumpStack = new StackPanel
        {
            Orientation = Orientation.Vertical, Spacing = 9,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 20, 16),
            Children = { _jumpTopBtn, _jumpPrevBtn, _jumpNextBtn, _jumpBottomBtn },
        };
        _thread.ScrollStateChanged += UpdateJumpButtons;

        Content = new Panel { Children = { mainDock, jumpStack } };

        _watcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size;
        _watcher.Changed += OnTranscriptChanged;
        _watcher.Created += OnTranscriptChanged;

        Closed += (_, _) => { try { _watcher.EnableRaisingEvents = false; } catch { } _watcher.Dispose(); };

        SetPlaceholderBody("Select a session to read its transcript.");
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (_renderSample) return;   // headless capture: keep the synthetic sample, skip the disk scan
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

    // ── Selector: the command-palette search modal ────────────────────────────────
    // Opens the search palette over a snapshot of the current session list; the chosen row loads here.
    private void OpenSearch()
    {
        var modal = new HistorySearchWindow(_entries, _selected?.SessionId);
        modal.Picked += SelectEntry;
        modal.Show(this);   // owned by the history window: stays above it, closes with it
    }

    // A session was chosen (palette pick / ShowSession): reflect it in the header and load its transcript.
    private void SelectEntry(HistoryEntry e)
    {
        _selected = e;
        _pendingSelect = null;
        _pendingSelectSet = false;
        UpdateCrumb();
        LoadTranscript(e);
    }

    // Reflects the current selection in the session-header crumb (name / path / id).
    private void UpdateCrumb()
    {
        if (_selected is { } e)
        {
            _projectText.Text = e.DisplayName;
            _pathText.Text = string.IsNullOrEmpty(e.Cwd) ? "(no working directory)" : e.Cwd;
        }
        else
        {
            _projectText.Text = "No session selected";
            _pathText.Text = "Search sessions to open one";
        }
        ShowSelectedId();
    }

    // ── Header session-id line ─────────────────────────────────────────────────────
    // Show the current selection's full id on a click-to-copy line, or hide it when nothing is selected.
    private void ShowSelectedId()
    {
        if (_selected is { SessionId: { Length: > 0 } id })
        {
            _idChipText.Text = id;
            ToolTip.SetTip(_idChip, "Copy session id");
            _idChip.IsVisible = true;
        }
        else
        {
            _idChip.IsVisible = false;
        }
    }

    private async void CopySessionId()
    {
        if (_selected is not { SessionId: { Length: > 0 } id }) return;
        try
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } cb) await cb.SetTextAsync(id);
        }
        catch { /* clipboard unavailable — nothing useful to do */ }
        _idChipText.Text = "copied ✓";
        DispatcherTimer.RunOnce(() => { if (_selected is { SessionId: { Length: > 0 } s }) _idChipText.Text = s; },
            TimeSpan.FromMilliseconds(1200));
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

                // Keep the current selection across a re-list (active sessions re-list often): refresh its
                // reference so the header's live/size read updates.
                if (_selected is { } sel)
                {
                    var match = _entries.FirstOrDefault(e => e.SessionId == sel.SessionId);
                    if (match is not null) { _selected = match; UpdateCrumb(); }
                }
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
        if (entry is not null) SelectEntry(entry);
    }

    // ── Transcript loading ────────────────────────────────────────────────────────
    private void LoadTranscript(HistoryEntry entry)
    {
        StopWatching();
        _conv = null;
        _consumedLines = 0;
        _loaded = entry;

        // Large transcripts can lag or exhaust memory — gate behind an explicit confirmation.
        if (entry.IsLarge)
        {
            var confirm = new Button { Content = $"Load anyway ({entry.SizeLabel})", Margin = new Thickness(0, 12, 0, 0) };
            confirm.Click += (_, _) => ParseAndRender(entry);
            _bodyHost.Child = new StackPanel
            {
                Margin = new Thickness(20),
                Children =
                {
                    new TextBlock { Text = "This transcript is large and may be slow to render.", Foreground = _p.Muted },
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
        System.Threading.Tasks.Task.Run(() => ReadCompleteLines(path)).ContinueWith(t =>
        {
            if (!t.IsCompletedSuccessfully) return;
            Dispatcher.UIThread.Post(() =>
            {
                if (!IsVisible || _loaded?.SessionId != entry.SessionId) return;
                BuildConversation(entry, t.Result);
            });
        });
    }

    // Decodes the transcript lines into a read-only conversation and binds the rich thread to it. A completed
    // (inactive) session is finalised so nothing reads as mid-turn; an active one is left open for tailing.
    private void BuildConversation(HistoryEntry entry, List<string> lines)
    {
        var conv = new SessionConversation();
        conv.UseHistorySession(entry.SessionId);
        foreach (var line in lines) conv.AppendTranscriptLine(line);
        if (!entry.IsActive) conv.FinalizeHistory();

        _conv = conv;
        _consumedLines = lines.Count;

        if (conv.Items.Count == 0)
        {
            SetPlaceholderBody("This transcript has no readable events yet.");
            if (entry.IsActive) StartWatching(entry.Path);   // it may fill in as the session runs
            return;
        }

        _thread.Cwd = entry.Cwd;
        ShowThread();
        _thread.Bind(conv);

        // Bind follows the tail (right for an active session). For a finished transcript, start at the top —
        // the natural place to begin reading. Posted below Bind's own scroll so it lands last; best-effort.
        if (!entry.IsActive)
            Dispatcher.UIThread.Post(
                () => { try { _thread.Offset = new Vector(_thread.Offset.X, 0); } catch { } },
                DispatcherPriority.Background);

        if (entry.IsActive) StartWatching(entry.Path);
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
        t.Tick += (_, _) => { t.Stop(); TailNow(); };
        return t;
    }

    // Re-reads the transcript off-thread and folds in only the newly-appended lines — the bound thread appends
    // them incrementally (no rebuild). A shrunk file (replaced/truncated) rebuilds from the top.
    private void TailNow()
    {
        if (_conv is null || _loaded is not { } entry || _tailBusy) return;
        _tailBusy = true;
        var path = entry.Path;
        System.Threading.Tasks.Task.Run(() => ReadCompleteLines(path)).ContinueWith(t =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                _tailBusy = false;
                if (!t.IsCompletedSuccessfully || !IsVisible || _conv is null || _loaded?.SessionId != entry.SessionId) return;
                var lines = t.Result;
                if (lines.Count < _consumedLines) { BuildConversation(entry, lines); return; }
                if (lines.Count == _consumedLines) return;

                // A conversation that started empty has no bound thread yet — swap the placeholder for it now.
                if (!ReferenceEquals(_bodyHost.Child, _thread))
                {
                    _thread.Cwd = entry.Cwd;
                    ShowThread();
                    _thread.Bind(_conv);
                }
                for (int i = _consumedLines; i < lines.Count; i++) _conv.AppendTranscriptLine(lines[i]);
                _consumedLines = lines.Count;
            });
        });
    }

    // ── Rendering surface ─────────────────────────────────────────────────────────
    private void ShowThread()
    {
        if (!ReferenceEquals(_bodyHost.Child, _thread)) _bodyHost.Child = _thread;
        UpdateJumpButtons();   // initial state; ScrollStateChanged refines it once the thread has laid out
    }

    private void SetPlaceholderBody(string text)
    {
        _bodyHost.Child = new TextBlock { Margin = new Thickness(20), Foreground = _p.Muted, Text = text };
        UpdateJumpButtons();   // off the thread — hide the floating nav
    }

    // ── Floating navigation ───────────────────────────────────────────────────────
    private Border JumpButton(string glyph, string tip, Action onClick)
    {
        var b = new Border
        {
            Width = 34, Height = 34, CornerRadius = new CornerRadius(17),
            Background = _p.Raised2, BorderBrush = _p.Border, BorderThickness = new Thickness(1),
            Cursor = new Cursor(StandardCursorType.Hand), IsVisible = false,
            BoxShadow = BoxShadows.Parse("0 6 18 0 #40000000"), [ToolTip.TipProperty] = tip,
            Child = new TextBlock
            {
                Text = glyph, FontSize = 15, Foreground = _p.Muted, FontWeight = FontWeight.Bold,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
        // Hover only reacts when the button is live — a disabled button (IsEnabled=false) gets no pointer events.
        b.PointerEntered += (_, _) => b.Background = _p.Border;
        b.PointerExited += (_, _) => b.Background = _p.Raised2;
        b.PointerReleased += (_, e) => { if (e.InitialPressMouseButton == MouseButton.Left) onClick(); };
        return b;
    }

    // The four buttons hold a fixed column (top / prev / next / bottom) so they never jump around; each is
    // enabled only when it would move the view (something above/below, a prompt above/below), and dimmed +
    // click-through-disabled otherwise. The whole column hides only when the body is a placeholder, not the thread.
    private void UpdateJumpButtons()
    {
        bool onThread = ReferenceEquals(_bodyHost.Child, _thread);
        _jumpTopBtn.IsVisible = _jumpPrevBtn.IsVisible = _jumpNextBtn.IsVisible = _jumpBottomBtn.IsVisible = onThread;
        SetJumpEnabled(_jumpTopBtn, onThread && !_thread.AtTop);
        SetJumpEnabled(_jumpPrevBtn, onThread && _thread.HasPromptAbove);
        SetJumpEnabled(_jumpNextBtn, onThread && _thread.HasPromptBelow);
        SetJumpEnabled(_jumpBottomBtn, onThread && !_thread.AtBottom);
    }

    // A disabled jump button keeps its slot but reads as inert: dimmed, resting fill, no pointer/hover.
    private void SetJumpEnabled(Border b, bool enabled)
    {
        b.IsEnabled = enabled;
        b.Opacity = enabled ? 1 : 0.32;
        b.Background = _p.Raised2;   // clear any lingering hover fill when it goes inert
    }

    /// <summary>HeadlessRenderer hook: binds the read-only thread to a synthetic conversation (no transcript on
    /// disk, no session-list scan) so the rich history render can be captured. Call before <c>Show()</c>.</summary>
    internal void ShowSampleForRender(string cwd, string? userPrompt, IEnumerable<SessionEvent> events)
    {
        _renderSample = true;
        var conv = new SessionConversation();
        if (userPrompt is not null) conv.AddUserPrompt(userPrompt);
        foreach (var ev in events) conv.Apply(ev);
        conv.FinalizeHistory();
        _conv = conv;
        _thread.Cwd = cwd;
        // Populate the session header so the render shows a realistic crumb (name / path / id).
        var project = System.IO.Path.GetFileName(cwd.TrimEnd('\\', '/'));
        _selected = new HistoryEntry("71506f61-782c-4c76-a821-df2eda08074f", project, cwd, "", DateTime.Now, true);
        UpdateCrumb();
        ShowThread();
        _thread.Bind(conv);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────
    // Reads the transcript's complete (newline-terminated) lines with a shared handle — the file is written
    // live. A trailing partial line (a half-written record) is left out and picked up on the next read.
    private static List<string> ReadCompleteLines(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs);
        var text = sr.ReadToEnd();
        var lines = new List<string>();
        int start = 0;
        for (int i = 0; i < text.Length; i++)
            if (text[i] == '\n')
            {
                lines.Add(text[start..i].TrimEnd('\r'));
                start = i + 1;
            }
        return lines;
    }
}

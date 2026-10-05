using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Perch.Avalonia.Theming;
using Perch.Avalonia.Views;
using Perch.Data;
using Perch.Data.Control;

namespace Perch.Avalonia.Windows;

// Background sub-agents, surfaced two ways. Under the header, a tab strip: "Session" plus one tab per working
// sub-agent/teammate; a sub-agent tab swaps the thread for that agent's own transcript, tailed live and read-only
// (the composer gives way to a note, since a sub-agent takes its instructions from the session). At the bottom of
// the chat, above the composer, a chip per working agent with a spinner and what it's doing, so a session waiting
// on background work doesn't look frozen; a chip opens that agent's tab. Both are fed by the same SessionMonitor scan
// that drives the overlay (UpdateBackgroundActivity). When the open agent stops working, the view returns to the
// session.
internal sealed partial class SessionWindow
{
    // How long the open tab's agent may be missing from the scans before the view returns to the session. A working
    // agent can blink off the roster between two records of one reply (its tail is briefly a text block, which reads
    // as a finished turn), so one missed scan isn't proof it has stopped.
    private static readonly TimeSpan AgentGoneGrace = TimeSpan.FromSeconds(5);
    // How often the open agent's transcript is tailed. Unchanged, a tick costs a stat.
    private static readonly TimeSpan AgentTailInterval = TimeSpan.FromMilliseconds(750);

    private Border _agentTabStrip = null!;
    private WrapPanel _agentTabs = null!;
    private Border _sessionTab = null!;
    private TextBlock _sessionTabText = null!;
    private Border _agentChipsRow = null!;
    private WrapPanel _agentChips = null!;
    private readonly Dictionary<string, AgentPill> _agentTabById = new();
    private readonly Dictionary<string, AgentPill> _agentChipById = new();

    // The agent view: a second thread (so the session's own keeps its scroll and expanded cards), a placeholder for
    // while it loads or has nothing to show, and the note that stands in for the composer.
    private Panel _agentHost = null!;
    private SessionThreadView _agentThread = null!;
    private TextBlock _agentPlaceholder = null!;
    private Border _agentFooter = null!;
    private TextBlock _agentFooterText = null!;

    private IReadOnlyList<SubAgent> _runningAgents = [];
    private SubAgent? _openAgent;               // the agent whose tab is open; null = the session
    private int _agentGen;                      // drops a load/tail that lands after the tab changed
    private SessionConversation? _agentConv;
    private TranscriptLineTail? _agentTail;
    private string? _agentPath;
    private bool _agentTailBusy;
    private DispatcherTimer? _agentTailTimer;
    private DispatcherTimer? _agentGoneTimer;

    private bool AgentViewOpen => _openAgent is not null;

    // The thread on screen — the jump buttons drive whichever it is.
    private SessionThreadView ActiveThread => AgentViewOpen ? _agentThread : _thread;

    // A tab or chip, kept per agent id and updated in place: re-adding one restarts its spinner, so the panel's
    // children are only re-seated when the set of agents changes.
    private sealed record AgentPill(Border Root, TextBlock Label, TextBlock? Activity, Control Spinner);

    private void BuildAgentChrome()
    {
        // ── Header tabs ──
        _sessionTabText = TabLabel("Session");
        _sessionTab = TabShell(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 7, VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new Ellipse { Width = 7, Height = 7, Fill = _p.Brand, VerticalAlignment = VerticalAlignment.Center },
                _sessionTabText,
            },
        });
        _sessionTab[ToolTip.TipProperty] = "The session's conversation";
        _sessionTab.PointerReleased += (_, e) => { if (e.InitialPressMouseButton == MouseButton.Left) CloseAgentView(); };
        _agentTabs = new WrapPanel { Orientation = Orientation.Horizontal, Children = { _sessionTab } };
        _agentTabStrip = new Border
        {
            Background = _p.Surface, BorderBrush = _p.BorderSoft, BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(14, 6, 14, 0), IsVisible = false, [DockPanel.DockProperty] = Dock.Top,
            Child = _agentTabs,
        };

        // ── Bottom-of-chat chips ──
        _agentChips = new WrapPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var caption = new TextBlock
        {
            Text = "RUNNING", FontFamily = _p.Mono, FontSize = 10.5, LetterSpacing = 1.1, Foreground = _p.Faint,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 10, 0),
            [DockPanel.DockProperty] = Dock.Left,
        };
        _agentChipsRow = new Border
        {
            MaxWidth = SessionPalette.ThreadMaxWidth, Margin = new Thickness(0, 0, 0, 8), IsVisible = false,
            Child = new DockPanel { Children = { caption, _agentChips } },
        };

        // ── The agent view ──
        _agentThread = new SessionThreadView(_p) { IsVisible = false };
        _agentThread.OpenFileRequested += p => OpenFileInViewerRequested?.Invoke(p);
        _agentThread.ViewDiffRequested += p => ViewFileDiffRequested?.Invoke(p);
        _agentThread.ScrollStateChanged += UpdateJumpButtons;
        _agentPlaceholder = new TextBlock
        {
            FontFamily = _p.Body, FontSize = 13, Foreground = _p.Muted, IsVisible = false,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 420,
        };
        _agentHost = new Panel { IsVisible = false, Children = { _agentThread, _agentPlaceholder } };

        _agentFooterText = new TextBlock
        {
            FontFamily = _p.Body, FontSize = 12.5, Foreground = _p.Muted, VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        };
        var back = new SessionButton(_p, "Back to session", SessionButtonKind.Quiet, "esc", compact: true)
        {
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0),
            [DockPanel.DockProperty] = Dock.Right,
        };
        back.Click += CloseAgentView;
        _agentFooter = new Border
        {
            MaxWidth = SessionPalette.ThreadMaxWidth, Background = _p.Raised, BorderBrush = _p.BorderSoft,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(15), Padding = new Thickness(16, 10, 10, 10),
            IsVisible = false,
            // A DockPanel throughout (not a horizontal StackPanel), so the note is width-bounded and wraps short of
            // the button.
            Child = new DockPanel
            {
                Children =
                {
                    back,
                    new LoadingSpinner
                    {
                        Width = 14, Height = 14, Thickness = 1.8, Stroke = _p.Violet, VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(0, 0, 10, 0), [DockPanel.DockProperty] = Dock.Left,
                    },
                    _agentFooterText,
                },
            },
        };
    }

    /// <summary>
    /// Mirrors the floating overlay's live sub-agent view onto this window: the header tabs and the bottom-of-chat
    /// chips list each background sub-agent/teammate currently working under the session, from the very same
    /// <see cref="ClaudeSession"/> the overlay renders (the app resolves it by id and pushes it after each scan).
    /// A controlled session runs its sub-agents in the background, so this is the only place their activity
    /// surfaces inside the window. Passing null — or a session with nothing running — hides both. Call on the UI
    /// thread.
    /// </summary>
    public void UpdateBackgroundActivity(ClaudeSession? mine)
    {
        // Only agents actually working now — an idle or interrupted (stale) teammate isn't "running".
        // SelfAndDescendants so a sub-agent nested under another still shows.
        var running = mine?.SubAgents
            .SelectMany(a => a.SelfAndDescendants())
            .Where(a => !a.IsIdle && !a.IsStale)
            .ToList() ?? [];
        ApplyRunningAgents(running);
    }

    private void ApplyRunningAgents(IReadOnlyList<SubAgent> running)
    {
        _runningAgents = running;
        if (_openAgent is { } open)
        {
            if (running.FirstOrDefault(a => a.AgentId == open.AgentId) is { } now)
            {
                _openAgent = now;   // fresher label/activity
                _agentGoneTimer?.Stop();
                UpdateAgentFooter();
            }
            else if (_agentGoneTimer is not { IsEnabled: true })
            {
                _agentGoneTimer ??= CreateAgentGoneTimer();
                _agentGoneTimer.Start();
            }
        }
        RenderAgents();
    }

    private DispatcherTimer CreateAgentGoneTimer()
    {
        var t = new DispatcherTimer { Interval = AgentGoneGrace };
        t.Tick += (_, _) =>
        {
            t.Stop();
            if (_closed || _openAgent is not { } open || _runningAgents.Any(a => a.AgentId == open.AgentId)) return;
            CloseAgentView();
            ShowToast($"{AgentLabel(open)} has finished — back to the session", _p.Violet);
        };
        return t;
    }

    // Paints the tabs and the chips from the current roster and the open tab.
    private void RenderAgents()
    {
        bool attached = _session is not null;
        // The open agent keeps its tab through the grace window even while it's missing from the roster.
        var tabs = _openAgent is { } open && !_runningAgents.Any(a => a.AgentId == open.AgentId)
            ? _runningAgents.Append(open).ToList()
            : _runningAgents;
        SyncPills(_agentTabs, _agentTabById, tabs, BuildAgentTab, UpdateAgentTab, keepFirst: 1);
        StyleTab(_sessionTab, _sessionTabText, selected: !AgentViewOpen, _p.Brand);
        _agentTabStrip.IsVisible = attached && tabs.Count > 0;

        SyncPills(_agentChips, _agentChipById, _runningAgents, BuildAgentChip, UpdateAgentChip);
        _agentChipsRow.IsVisible = attached && !AgentViewOpen && _runningAgents.Count > 0;
    }

    private static void SyncPills(Panel host, Dictionary<string, AgentPill> cache, IReadOnlyList<SubAgent> agents,
        Func<string, AgentPill> build, Action<AgentPill, SubAgent> update, int keepFirst = 0)
    {
        var ids = agents.Select(a => a.AgentId).ToHashSet();
        foreach (var stale in cache.Keys.Where(k => !ids.Contains(k)).ToList()) cache.Remove(stale);
        var roots = new List<Control>(agents.Count);
        foreach (var a in agents)
        {
            if (!cache.TryGetValue(a.AgentId, out var pill)) cache[a.AgentId] = pill = build(a.AgentId);
            update(pill, a);
            roots.Add(pill.Root);
        }
        if (host.Children.Skip(keepFirst).SequenceEqual(roots)) return;
        while (host.Children.Count > keepFirst) host.Children.RemoveAt(host.Children.Count - 1);
        foreach (var r in roots) host.Children.Add(r);
    }

    // ── Tabs ──

    private TextBlock TabLabel(string text) => new()
    {
        Text = text, FontFamily = _p.Body, FontSize = 12.5, FontWeight = FontWeight.SemiBold,
        VerticalAlignment = VerticalAlignment.Center,
    };

    // A tab: a top-rounded cell with an accent underline when selected. Hover lifts an unselected one.
    private Border TabShell(Control content)
    {
        var tab = new Border
        {
            Padding = new Thickness(12, 6, 12, 7), Margin = new Thickness(0, 0, 4, 0),
            CornerRadius = new CornerRadius(8, 8, 0, 0), BorderThickness = new Thickness(0, 0, 0, 2),
            Background = Brushes.Transparent, BorderBrush = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand), Child = content,
        };
        tab.PointerEntered += (_, _) => { if (tab.Tag is not true) tab.Background = _p.Raised; };
        tab.PointerExited += (_, _) => { if (tab.Tag is not true) tab.Background = Brushes.Transparent; };
        return tab;
    }

    private void StyleTab(Border tab, TextBlock label, bool selected, IBrush accent)
    {
        tab.Tag = selected;
        tab.Background = selected ? _p.Raised : Brushes.Transparent;
        tab.BorderBrush = selected ? accent : Brushes.Transparent;
        label.Foreground = selected ? _p.Text : _p.Muted;
    }

    private AgentPill BuildAgentTab(string agentId)
    {
        var spinner = new LoadingSpinner { Width = 12, Height = 12, Thickness = 1.8, Stroke = _p.Violet, VerticalAlignment = VerticalAlignment.Center };
        var label = TabLabel("");
        var tab = TabShell(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 7, VerticalAlignment = VerticalAlignment.Center,
            Children = { spinner, label },
        });
        tab.PointerReleased += (_, e) => { if (e.InitialPressMouseButton == MouseButton.Left) OpenAgentTab(agentId); };
        return new AgentPill(tab, label, null, spinner);
    }

    private void UpdateAgentTab(AgentPill tab, SubAgent a)
    {
        var label = AgentLabel(a);
        tab.Label.Text = ClipChip(label, 30);
        bool working = _runningAgents.Any(r => r.AgentId == a.AgentId);
        tab.Spinner.IsVisible = working;
        tab.Root[ToolTip.TipProperty] = (string.IsNullOrWhiteSpace(a.Activity) ? label : $"{label} — {a.Activity}")
            + "\nClick to watch its log";
        StyleTab(tab.Root, tab.Label, selected: _openAgent?.AgentId == a.AgentId, _p.Violet);
    }

    // ── Chips ──

    // One chip per working agent at the bottom of the chat: a spinner in the theme's sub-agent hue, its label and
    // what it's doing. A click opens its tab.
    private AgentPill BuildAgentChip(string agentId)
    {
        var spinner = new LoadingSpinner { Width = 12, Height = 12, Thickness = 1.8, Stroke = _p.Violet, VerticalAlignment = VerticalAlignment.Center };
        var label = new TextBlock
        {
            FontFamily = _p.Body, FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = _p.Text,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var activity = new TextBlock
        {
            FontFamily = _p.Mono, FontSize = 11.5, Foreground = _p.Faint, VerticalAlignment = VerticalAlignment.Center,
        };
        var chip = Pill(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 7, VerticalAlignment = VerticalAlignment.Center,
            Children = { spinner, label, activity },
        }, _p.VioletWash, _p.VioletLine);
        chip.Margin = new Thickness(0, 2, 8, 2);
        chip.Cursor = new Cursor(StandardCursorType.Hand);
        chip.PointerEntered += (_, _) => chip.BorderBrush = _p.Violet;
        chip.PointerExited += (_, _) => chip.BorderBrush = _p.VioletLine;
        chip.PointerReleased += (_, e) => { if (e.InitialPressMouseButton == MouseButton.Left) OpenAgentTab(agentId); };
        return new AgentPill(chip, label, activity, spinner);
    }

    private void UpdateAgentChip(AgentPill chip, SubAgent a)
    {
        var label = AgentLabel(a);
        chip.Label.Text = ClipChip(label, 34);
        bool hasActivity = !string.IsNullOrWhiteSpace(a.Activity);
        chip.Activity!.Text = hasActivity ? ClipChip(a.Activity!, 40) : "";
        chip.Activity.IsVisible = hasActivity;
        chip.Root[ToolTip.TipProperty] = (hasActivity ? $"{label} — {a.Activity}" : label) + "\nClick to watch its log";
    }

    // A teammate goes by its name, a plain sub-agent by the task's description, else by its type.
    private static string AgentLabel(SubAgent a)
    {
        var label = a.IsTeammate
            ? (string.IsNullOrEmpty(a.Name) ? a.Description : a.Name!)
            : (string.IsNullOrEmpty(a.Description) ? a.AgentType : a.Description);
        return string.IsNullOrWhiteSpace(label) ? "sub-agent" : label;
    }

    // ── The agent view ──

    // Opens a working agent's tab: its transcript, read off the UI thread, then tailed while the tab stays open.
    private void OpenAgentTab(string agentId)
    {
        if (_openAgent?.AgentId == agentId) return;
        if (_runningAgents.FirstOrDefault(a => a.AgentId == agentId) is not { } agent) return;
        if (_session?.SessionId is not { } sessionId) return;

        ResetAgentView();
        _openAgent = agent;
        int gen = _agentGen;
        ShowAgentPlaceholder("Opening the sub-agent's log…");
        ApplyAgentView();

        var cwd = _session.Cwd;
        var tail = new TranscriptLineTail();
        Task.Run(() =>
        {
            var path = SubAgentReader.TranscriptPath(sessionId, cwd, agentId);
            var lines = path is null ? null : tail.Read(path)?.Lines;
            return (Path: path, Conv: lines is null ? null : DecodeAgentConversation(sessionId, lines));
        }).ContinueWith(t => Dispatcher.UIThread.Post(() =>
        {
            if (_closed || gen != _agentGen) return;
            if (!t.IsCompletedSuccessfully || t.Result.Path is null)
            {
                ShowAgentPlaceholder("This sub-agent has no log of its own to show.\n"
                    + "Older Claude Code versions keep a sub-agent's work inside the session's transcript.");
                return;
            }
            _agentPath = t.Result.Path;
            _agentTail = tail;
            if (t.Result.Conv is { } conv) ShowAgentConversation(conv);
            else ShowAgentPlaceholder("Waiting for the sub-agent's log…");
            _agentTailTimer ??= CreateAgentTailTimer();
            _agentTailTimer.Start();
        }));
    }

    private static SessionConversation DecodeAgentConversation(string sessionId, List<string> lines)
    {
        var conv = new SessionConversation();
        conv.UseHistorySession(sessionId);
        foreach (var line in lines)
            if (SessionConversation.ParseTranscriptLine(line, sessionId, includeSidechain: true) is { } p)
                conv.AppendParsedTranscriptLine(p);
        conv.SetLiveTail(true);   // the tab is only open while the agent works
        return conv;
    }

    private void ShowAgentConversation(SessionConversation conv)
    {
        _agentConv = conv;
        if (conv.Items.Count == 0) { ShowAgentPlaceholder("Waiting for the sub-agent's log…"); return; }
        _agentThread.Cwd = _session?.Cwd ?? _cwd;
        _agentThread.Bind(conv);
        _agentPlaceholder.IsVisible = false;
        _agentThread.IsVisible = true;
        UpdateJumpButtons();
    }

    private void ShowAgentPlaceholder(string text)
    {
        _agentPlaceholder.Text = text;
        _agentPlaceholder.IsVisible = true;
        _agentThread.IsVisible = false;
        UpdateJumpButtons();
    }

    // What a tail read produced: a whole new conversation (the file was truncated or replaced), or the appended
    // lines, already decoded.
    private sealed record AgentTailResult(SessionConversation? Rebuilt, List<SessionConversation.ParsedTranscriptLine> Appended);

    private DispatcherTimer CreateAgentTailTimer()
    {
        var t = new DispatcherTimer { Interval = AgentTailInterval };
        t.Tick += (_, _) => TailAgent();
        return t;
    }

    // Reads only what the agent appended since the last tick and decodes it off the UI thread; the bound thread then
    // grows in place. A truncated or replaced file rebuilds from the top.
    private void TailAgent()
    {
        if (_agentTailBusy || _agentTail is not { } tail || _agentPath is not { } path || _session?.SessionId is not { } sessionId)
            return;
        _agentTailBusy = true;
        int gen = _agentGen;
        Task.Run(() =>
        {
            if (tail.Read(path) is not { } read) return new AgentTailResult(null, []);
            if (read.Reset) return new AgentTailResult(DecodeAgentConversation(sessionId, read.Lines), []);
            var parsed = new List<SessionConversation.ParsedTranscriptLine>(read.Lines.Count);
            foreach (var line in read.Lines)
                if (SessionConversation.ParseTranscriptLine(line, sessionId, includeSidechain: true) is { } p) parsed.Add(p);
            return new AgentTailResult(null, parsed);
        }).ContinueWith(t => Dispatcher.UIThread.Post(() =>
        {
            if (gen != _agentGen) return;   // a newer tab owns the busy flag now (ResetAgentView cleared it)
            _agentTailBusy = false;
            if (_closed || !t.IsCompletedSuccessfully) return;
            var (rebuilt, appended) = t.Result;
            if (rebuilt is not null) { ShowAgentConversation(rebuilt); return; }
            if (appended.Count == 0) return;
            if (_agentConv is null)
            {
                var conv = new SessionConversation();
                conv.UseHistorySession(sessionId);
                conv.SetLiveTail(true);
                _agentConv = conv;
            }
            foreach (var line in appended) _agentConv.AppendParsedTranscriptLine(line);
            if (!_agentThread.IsVisible) ShowAgentConversation(_agentConv);   // it started empty
        }));
    }

    /// <summary>Back to the session's own conversation (the "Session" tab, Esc, or the agent stopping).</summary>
    private void CloseAgentView()
    {
        if (!AgentViewOpen) return;
        ResetAgentView();
        ApplyAgentView();
        if (CanCompose) _composer.Focus();
    }

    // Drops the open agent's view state: its load/tail (by generation), timers and the bound conversation.
    private void ResetAgentView()
    {
        _agentGen++;
        _agentTailTimer?.Stop();
        _agentGoneTimer?.Stop();
        _agentTailBusy = false;
        _agentThread.Unbind();
        _agentConv = null;
        _agentTail = null;
        _agentPath = null;
        _openAgent = null;
    }

    // Swaps the centre and the composer between the session and the open agent, and repaints the tabs.
    private void ApplyAgentView()
    {
        bool agent = AgentViewOpen;
        if (agent)
        {
            if (_findBar.IsVisible) CloseFind();   // find searches the session's thread
            ClosePalette();
            CloseMention();
        }
        _thread.IsVisible = !agent && !_launcher.IsVisible;
        _agentHost.IsVisible = agent;
        _composerFrame.IsVisible = !agent;
        _agentFooter.IsVisible = agent;
        UpdateAgentFooter();
        RenderAgents();
        UpdateJumpButtons();
    }

    private void UpdateAgentFooter()
    {
        if (_openAgent is { } a)
            _agentFooterText.Text = $"Watching {AgentLabel(a)} — read-only. A sub-agent takes its instructions from the session, so there's nothing to reply to here.";
    }

    // A prompt that needs the user (permission, question, plan) waits in the session's thread, so leave the agent
    // view for it rather than leave the session blocked behind a tab.
    private void ReturnToSessionIfNeeded()
    {
        if (AgentViewOpen && Conv.PendingPermission is not null) CloseAgentView();
    }

    // The window's session changed or went away: no agents, no agent view.
    private void ClearAgents()
    {
        ResetAgentView();
        _runningAgents = [];
        ApplyAgentView();
    }

    // ── Headless render hooks ──

    /// <summary>Render-only: shows a roster of working agents (the tabs and the bottom chips) without a scan.</summary>
    internal void ShowAgentsForRender(IReadOnlyList<SubAgent> running) => ApplyRunningAgents(running);

    /// <summary>Render-only: opens <paramref name="agentId"/>'s tab over a synthetic transcript (no file).</summary>
    internal void OpenAgentSampleForRender(string agentId, string prompt, IEnumerable<SessionEvent> events)
    {
        if (_runningAgents.FirstOrDefault(a => a.AgentId == agentId) is not { } agent) return;
        ResetAgentView();
        _openAgent = agent;
        ApplyAgentView();
        var conv = new SessionConversation();
        conv.AppendParsedTranscriptLine(new SessionConversation.ParsedTranscriptLine(prompt, null, []));
        foreach (var ev in events) conv.Apply(ev);
        conv.SetLiveTail(true);
        ShowAgentConversation(conv);
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Perch.Avalonia.Services;
using Perch.Avalonia.Theming;
using Perch.Avalonia.Views;
using Perch.Data;
using Perch.Data.Control;

namespace Perch.Avalonia.Windows;

// Rewind and fork (docs/session-rewind-plan.md, R3-R5). Rewind is a mode on the thread: the composer gives way to
// a bar, the thread marks the prompts that can anchor a rewind, and the user scrolls, clicks or arrows to the
// point. The bar says what would go (prompts, and the files a dry-run rewind_files reports) and offers the
// conversation, the code, both, or a fork from there.
internal sealed partial class SessionWindow
{
    /// <summary>A fork was asked for: the new session's launch options, plus the prompt to put in its composer
    /// (the one the fork branched before) and that prompt's attachments.</summary>
    public event Action<SessionLaunchOptions, string?, IReadOnlyList<MessageAttachment>?>? ForkRequested;

    private sealed class RewindState
    {
        public required List<UserMessageItem> Targets;
        public required Dictionary<UserMessageItem, PromptRef> Refs;
        public int Index;
        public RewindFilesEvent? Preview;
        public int PreviewGen;
        public bool Busy;
    }

    private RewindState? _rewind;
    private int _rewindOpenGen;
    private DateTime _lastIdleEsc;
    private Border _rewindBar = null!;
    private TextBlock _rewindTitle = null!, _rewindCode = null!, _rewindCaveat = null!;
    private WrapPanel _rewindActions = null!;

    // How long to settle on a selection before asking for its code preview, so arrowing through prompts doesn't
    // send a request per prompt passed.
    private static readonly TimeSpan PreviewDebounce = TimeSpan.FromMilliseconds(140);

    // Two Esc presses this close together, with nothing to dismiss or stop, open rewind mode (the TUI's gesture).
    private static readonly TimeSpan DoubleEscWindow = TimeSpan.FromMilliseconds(600);

    private bool RewindOpen => _rewindBar.IsVisible;

    private Border BuildRewindBar()
    {
        _rewindTitle = new TextBlock
        {
            FontFamily = _p.Body, FontSize = 14, FontWeight = FontWeight.SemiBold, Foreground = _p.Title,
            TextWrapping = TextWrapping.Wrap,
        };
        _rewindCode = new TextBlock { FontFamily = _p.Mono, FontSize = 12, Foreground = _p.Muted, TextWrapping = TextWrapping.Wrap };
        _rewindCaveat = new TextBlock
        {
            FontFamily = _p.Body, FontSize = 11.5, Foreground = _p.Faint, TextWrapping = TextWrapping.Wrap,
            Text = "Code rewind restores edits made by Claude's file tools. Changes made by shell commands aren't tracked.",
        };
        _rewindActions = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        var hint = new TextBlock
        {
            Text = "click a prompt or ↑↓ to choose   ·   ↵ rewind   ·   esc cancel",
            FontFamily = _p.Mono, FontSize = 10.5, Foreground = _p.Faint,
        };
        var badge = new Border
        {
            Background = _p.BrandWash, BorderBrush = _p.BrandLine, BorderThickness = new Thickness(1),
            CornerRadius = SessionPalette.PillRadius, Padding = new Thickness(8, 1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = "↺ REWIND", FontFamily = _p.Mono, FontSize = 10.5, LetterSpacing = 1, Foreground = _p.Brand },
        };
        _rewindBar = new Border
        {
            Background = _p.Raised, BorderBrush = _p.BrandLine, BorderThickness = new Thickness(1),
            CornerRadius = SessionPalette.CardRadius, Padding = new Thickness(16, 12), IsVisible = false,
            MaxWidth = SessionPalette.ThreadMaxWidth + 44,
            Child = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    new DockPanel
                    {
                        LastChildFill = true,
                        Children = { WithDock(badge, Dock.Left), WithMargin(_rewindTitle, new Thickness(10, 0, 0, 0)) },
                    },
                    _rewindCode, _rewindCaveat, _rewindActions, hint,
                },
            },
        };
        return _rewindBar;

        static Control WithDock(Control c, Dock d) { DockPanel.SetDock(c, d); return c; }
        static Control WithMargin(Control c, Thickness m) { c.Margin = m; c.VerticalAlignment = VerticalAlignment.Center; return c; }
    }

    /// <summary>Opens rewind mode (the composer overflow's "Rewind…", <c>/rewind</c>, Esc Esc, or a prompt's
    /// rewind button). <paramref name="initial"/> is the prompt to start on; null starts on the latest.</summary>
    public void OpenRewind(UserMessageItem? initial = null)
    {
        if (RewindOpen)
        {
            if (initial is not null) SelectRewindTarget(initial);
            return;
        }
        if (_session is not { } s || s.HasEnded || s.IsRestarting)
        {
            ShowToast("Rewind needs the session open: resume it first.", _p.Await);
            return;
        }
        if (Conv.TurnActive)
        {
            ShowToast("Claude is still working. Press Esc to stop the turn, then rewind.", _p.Await);
            return;
        }
        if (AgentViewOpen) CloseAgentView();
        var candidates = Conv.RewindCandidates();
        if (candidates.Count == 0)
        {
            ShowToast("Nothing to rewind to yet.", _p.Await);
            return;
        }

        int gen = ++_rewindOpenGen;
        ShowRewindBar(true);
        _rewindTitle.Text = "Finding your prompts in the transcript…";
        _rewindCode.Text = "";
        _rewindActions.Children.Clear();
        _rewindActions.Children.Add(RewindButton("Cancel", SessionButtonKind.Quiet, CloseRewind));

        string? sessionId = s.SessionId, cwd = s.Cwd, leaf = s.ResumeAt;
        var texts = candidates.Select(c => c.Text).ToList();
        Task.Run(() =>
        {
            var path = sessionId is null ? null : TranscriptLocator.Resolve(sessionId, cwd);
            if (path is null) return null;
            var prompts = RewindPoints.Read(TranscriptChain.ActiveLines(() => TranscriptScan.ReadLines(path), leaf));
            return RewindPoints.Match(texts, prompts);
        }).ContinueWith(t => Dispatcher.UIThread.Post(() =>
        {
            if (_closed || gen != _rewindOpenGen || !RewindOpen || !ReferenceEquals(_session, s)) return;
            var matched = t.IsCompletedSuccessfully ? t.Result : null;
            var refs = new Dictionary<UserMessageItem, PromptRef>();
            for (int i = 0; matched is not null && i < candidates.Count; i++)
                if (matched[i] is { } r)
                {
                    refs[candidates[i]] = r;
                    candidates[i].Uuid ??= r.Uuid;
                }
            var targets = candidates.Where(refs.ContainsKey).ToList();
            if (targets.Count == 0)
            {
                _rewindTitle.Text = "Couldn't find these prompts in the session's transcript, so there's nowhere to rewind to.";
                return;
            }
            _rewind = new RewindState { Targets = targets, Refs = refs };
            int start = initial is not null && targets.IndexOf(initial) is >= 0 and var at ? at : targets.Count - 1;
            SelectRewindIndex(start);
        }));
    }

    private void CloseRewind()
    {
        _rewindOpenGen++;
        _rewind = null;
        _thread.SetRewindView(null, null);
        ShowRewindBar(false);
        if (CanCompose) _composer.Focus();
    }

    private void ShowRewindBar(bool shown)
    {
        _rewindBar.IsVisible = shown;
        _composerFrame.IsVisible = !shown && !AgentViewOpen;
        if (shown) _agentChipsRow.IsVisible = false;
        else RenderAgents();
    }

    private void SelectRewindTarget(UserMessageItem item)
    {
        if (_rewind is { } r && r.Targets.IndexOf(item) is >= 0 and var i) SelectRewindIndex(i);
    }

    private void SelectRewindIndex(int index)
    {
        if (_rewind is not { } r) return;
        r.Index = Math.Clamp(index, 0, r.Targets.Count - 1);
        r.Preview = null;
        _thread.SetRewindView(r.Targets[r.Index], r.Targets);
        RenderRewindBar();
        QueueRewindPreview(r);
    }

    // The dry-run rewind_files for the selection, once it has settled. Only a running process can answer.
    private async void QueueRewindPreview(RewindState r)
    {
        int gen = ++r.PreviewGen;
        if (_session is not { IsRunning: true } s)
        {
            r.Preview = new RewindFilesEvent("", false, "start the session to rewind code");
            RenderRewindBar();
            return;
        }
        await Task.Delay(PreviewDebounce);
        if (!ReferenceEquals(_rewind, r) || gen != r.PreviewGen) return;
        var preview = await s.RewindFilesAsync(r.Refs[r.Targets[r.Index]].Uuid, dryRun: true);
        if (!ReferenceEquals(_rewind, r) || gen != r.PreviewGen) return;
        r.Preview = preview;
        RenderRewindBar();
    }

    private void RenderRewindBar()
    {
        if (_rewind is not { } r) return;
        var item = r.Targets[r.Index];
        var anchor = r.Refs[item];
        int dropped = Conv.Items.SkipWhile(i => !ReferenceEquals(i, item)).OfType<UserMessageItem>().Count();
        _rewindTitle.Text = $"Rewind to before “{SessionConversation.Preview(item.Text)}” · "
                          + (dropped == 1 ? "drops this prompt" : $"drops {dropped} prompts");

        var p = r.Preview;
        int files = p?.FilesChanged?.Count ?? 0;
        bool codeKnown = p is not null;
        bool canCode = p is { CanRewind: true } && files > 0;
        _rewindCode.Text = p switch
        {
            null => "code: checking what would change…",
            { CanRewind: false } => $"code: can't be rewound here ({p.Error ?? "no checkpoint"})",
            _ when files == 0 => "code: no file changes to undo",
            _ => $"code: {files} file{(files == 1 ? "" : "s")} · +{p.Insertions} −{p.Deletions}",
        };
        ToolTip.SetTip(_rewindCode, files > 0 ? string.Join("\n", p!.FilesChanged!) : null);
        _rewindCaveat.IsVisible = canCode;

        bool running = _session is { IsRunning: true };
        // The first prompt has nothing before it to keep: rewinding the conversation there is a /clear, which
        // needs the process.
        bool canConversation = anchor.ParentUuid is not null || running;
        _rewindActions.Children.Clear();
        if (canCode)
        {
            _rewindActions.Children.Add(RewindButton("Rewind conversation + code", SessionButtonKind.Primary,
                () => _ = ApplyRewindAsync(conversation: true, code: true), canConversation && !r.Busy));
            _rewindActions.Children.Add(RewindButton("Conversation only", SessionButtonKind.Ghost,
                () => _ = ApplyRewindAsync(conversation: true, code: false), canConversation && !r.Busy));
            _rewindActions.Children.Add(RewindButton("Code only", SessionButtonKind.Ghost,
                () => _ = ApplyRewindAsync(conversation: false, code: true), !r.Busy));
        }
        else
        {
            _rewindActions.Children.Add(RewindButton("Rewind conversation", SessionButtonKind.Primary,
                () => _ = ApplyRewindAsync(conversation: true, code: false), canConversation && !r.Busy && (codeKnown || !running)));
        }
        var fork = RewindButton("Fork from here", SessionButtonKind.Ghost, ForkFromRewind, anchor.ParentUuid is not null && !r.Busy);
        ToolTip.SetTip(fork, "Open a new session with the conversation up to here, leaving this one as it is");
        _rewindActions.Children.Add(fork);
        _rewindActions.Children.Add(RewindButton("Cancel", SessionButtonKind.Quiet, CloseRewind));
    }

    private SessionButton RewindButton(string label, SessionButtonKind kind, Action click, bool enabled = true)
    {
        var b = new SessionButton(_p, label, kind, compact: true) { Enabled = enabled, Margin = new Thickness(0, 0, 8, 6) };
        b.Click += () => { if (b.Enabled) click(); };
        return b;
    }

    // Enter: the primary action as offered (conversation + code when there's code to undo).
    private void ApplyRewindDefault()
    {
        if (_rewind is not { Busy: false } r || r.Preview is null && _session is { IsRunning: true }) return;
        bool canCode = r.Preview is { CanRewind: true } && (r.Preview.FilesChanged?.Count ?? 0) > 0;
        _ = ApplyRewindAsync(conversation: true, code: canCode);
    }

    private async Task ApplyRewindAsync(bool conversation, bool code)
    {
        if (_rewind is not { Busy: false } r || _session is not { } s) return;
        var item = r.Targets[r.Index];
        var anchor = r.Refs[item];
        r.Busy = true;
        RenderRewindBar();

        if (code)
        {
            var result = await s.RewindFilesAsync(anchor.Uuid, dryRun: false);
            if (!ReferenceEquals(_rewind, r)) return;
            if (!result.CanRewind)
            {
                r.Busy = false;
                RenderRewindBar();
                ShowToast($"Couldn't rewind the code: {result.Error ?? "no checkpoint"}");
                return;
            }
            int files = r.Preview?.FilesChanged?.Count ?? 0;
            Conv.AddNote($"restored {files} file{(files == 1 ? "" : "s")} to before “{SessionConversation.Preview(item.Text)}”");
        }

        if (conversation)
        {
            bool ok = anchor.ParentUuid is { } keep ? s.RewindConversation(item, keep) : ClearForFirstPrompt(s);
            if (!ok) ShowToast("Couldn't rewind the conversation right now.");
            else RefillComposer(item);
        }
        CloseRewind();
    }

    // Rewinding to before the very first prompt keeps nothing, which is an empty conversation: a /clear.
    private static bool ClearForFirstPrompt(PerchSession s)
    {
        if (!s.IsRunning) return false;
        s.SendPrompt("/clear");
        return true;
    }

    // The rewound prompt comes back to the composer to edit and send again, as the terminal does.
    private void RefillComposer(UserMessageItem item)
    {
        _composer.Text = item.Text;
        _composer.CaretIndex = item.Text.Length;
        ClearAttachments();
        foreach (var a in item.Attachments)
            if (File.Exists(a.Path)) AddAttachment(a);
    }

    /// <summary>Puts a prompt (and its attachments) into the composer of a fresh window, e.g. a fork's.</summary>
    public void PrefillComposer(string text, IReadOnlyList<MessageAttachment>? attachments)
    {
        _composer.Text = text;
        _composer.CaretIndex = text.Length;
        foreach (var a in attachments ?? [])
            if (File.Exists(a.Path)) AddAttachment(a);
    }

    private void ForkFromRewind()
    {
        if (_rewind is not { } r || _session is not { } s) return;
        var item = r.Targets[r.Index];
        if (r.Refs[item].ParentUuid is not { } keep) return;
        ForkRequested?.Invoke(s.ForkOptions(keep), item.Text, item.Attachments);
        CloseRewind();
    }

    /// <summary>Forks the whole session into a new one (<c>/fork</c>, the composer overflow).</summary>
    public void ForkSession()
    {
        if (_session is not { SessionId: { Length: > 0 } } s)
        {
            ShowToast("Start the session before forking it.", _p.Await);
            return;
        }
        ForkRequested?.Invoke(s.ForkOptions(null), null, null);
    }

    // Keys while the bar is up. Returns true when handled. Esc is handled in the window's Esc chain.
    private bool HandleRewindKey(KeyEventArgs e)
    {
        if (!RewindOpen || _rewind is not { } r) return false;
        switch (e.Key)
        {
            case Key.Up: SelectRewindIndex(r.Index - 1); return true;
            case Key.Down: SelectRewindIndex(r.Index + 1); return true;
            case Key.Home: SelectRewindIndex(0); return true;
            case Key.End: SelectRewindIndex(r.Targets.Count - 1); return true;
            case Key.Enter: ApplyRewindDefault(); return true;
            default: return false;
        }
    }

    // The tail of the Esc chain: a second press soon after the first, with an empty composer and nothing else to
    // dismiss or stop, opens rewind mode.
    private bool MaybeDoubleEscRewind()
    {
        if (_session is not ({ IsRunning: true } or { IsDormant: true }) || !string.IsNullOrEmpty(_composer.Text)) return false;
        var now = DateTime.UtcNow;
        if (now - _lastIdleEsc <= DoubleEscWindow)
        {
            _lastIdleEsc = default;
            OpenRewind();
            return true;
        }
        _lastIdleEsc = now;
        return false;
    }

    /// <summary>HeadlessRenderer: append more turns to the sample conversation, then open rewind mode on the prompt
    /// <paramref name="selectFromEnd"/> back from the latest, with <paramref name="preview"/> as its code preview. The
    /// transcript read is skipped: a render session has none, so the anchors are made up.</summary>
    internal void ShowRewindForRender(IEnumerable<(string Prompt, IReadOnlyList<SessionEvent> Events)> turns,
        RewindFilesEvent preview, int selectFromEnd)
    {
        foreach (var (prompt, events) in turns)
        {
            Conv.AddUserPrompt(prompt);
            foreach (var ev in events) Conv.Apply(ev);
        }
        var targets = Conv.RewindCandidates().ToList();
        var refs = new Dictionary<UserMessageItem, PromptRef>();
        for (int i = 0; i < targets.Count; i++)
            refs[targets[i]] = new PromptRef($"u{i}", i == 0 ? null : $"p{i}", targets[i].Text);
        ShowRewindBar(true);
        _rewind = new RewindState { Targets = targets, Refs = refs, Index = Math.Max(0, targets.Count - 1 - selectFromEnd), Preview = preview };
        _thread.SetRewindView(targets[_rewind.Index], targets);
        RenderRewindBar();
    }

    // A turn starting (say, from Remote Control) or the session going away ends the mode: the points it offered
    // may no longer be the conversation's.
    private void MaybeCloseRewindOnState()
    {
        if (RewindOpen && (_session is not { } s || s.HasEnded || Conv.TurnActive)) CloseRewind();
    }
}

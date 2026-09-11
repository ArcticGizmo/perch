using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Perch.Avalonia.Rendering;
using Perch.Avalonia.Theming;
using Perch.Data;
using Perch.Data.Control;

namespace Perch.Avalonia.Views;

/// <summary>
/// The conversation surface of the rich session UI: binds a <see cref="SessionConversation"/> and renders
/// one composed control per item — the user's warm bubble, Claude as open prose under the bird mark with
/// collapsible thinking and tool cards nested inside, permission prompts as first-class cards, and quiet
/// system notes. Updates are incremental: an item's control is kept and its parts re-synced on
/// <see cref="ConversationChange.Updated"/> (a streaming text block is swapped for a Markdown render when
/// it finalises; a tool card flips its status in place). Sticks to the bottom while the user hasn't
/// scrolled away. Visual tokens all come from <see cref="SessionPalette"/>.
/// </summary>
internal sealed class SessionThreadView : ScrollViewer
{
    private readonly SessionPalette _p;
    private readonly StackPanel _stack;
    private readonly Canvas _highlightLayer;   // translucent find-match rectangles, above the thread, click-through
    private readonly Dictionary<ConversationItem, ItemView> _views = new();
    private readonly Dictionary<CompactionItem, CompactionCard> _compactions = new();
    private readonly string _initials;
    private SessionConversation? _conv;
    private bool _stickToBottom = true;

    // Ctrl+F find. Each item's Root is wrapped in a Border (so a hidden/collapsed section can be located by
    // its visible card). Matches are painted as translucent rectangles in an overlay layer above the thread
    // (_highlightLayer): every occurrence dim, the current one brighter — so all hits are visible at a glance
    // for scanning, and the layer scrolls with the content it sits over. See Search/ShowMatch/ClearSearch.
    private readonly Dictionary<ConversationItem, Border> _wraps = new();
    private readonly List<FindMatch> _matches = new();   // every occurrence, in reading order
    private int _matchCurrent = -1;                       // index into _matches of the current match, or -1

    // One find hit. A visible occurrence carries its <see cref="Text"/> block + [Start,Start+Length) so its
    // exact glyph rectangles can be lit; a collapsed/non-text occurrence (<see cref="CardOnly"/>) lights the
    // whole <see cref="Anchor"/> card instead — collapsed sections fold to one hit so "next" skips their innards.
    private sealed class FindMatch
    {
        public required Control Anchor;
        public TextBlock? Text;
        public int Start;
        public int Length;
        public bool CardOnly;
    }

    // Every user-prompt row in order, so "jump to previous prompt" can walk upward through them; plus the cached
    // scroll state the window's floating jump buttons watch (recomputed on every scroll/layout change).
    private readonly List<Control> _userRows = new();
    private bool _atBottom = true;
    private bool _atTop = true;
    private bool _hasPromptAbove;
    private bool _hasPromptBelow;

    // A prompt whose top is within this many DIPs of the viewport top counts as "here", not "above" — so a
    // repeated ↑ steps to the next one up rather than re-selecting the prompt already pinned to the top.
    private const double PromptAboveEpsilon = 6;

    /// <summary>True when the view is scrolled to (or near) the tail — the "jump to bottom" button hides.</summary>
    public bool AtBottom => _atBottom;

    /// <summary>True when the view is scrolled to (or near) the very top — the "jump to top" button hides.</summary>
    public bool AtTop => _atTop;

    /// <summary>True when a user prompt sits above the viewport — the "jump to previous prompt" button shows.</summary>
    public bool HasPromptAbove => _hasPromptAbove;

    /// <summary>True when a user prompt sits below the viewport top — the "jump to next prompt" button shows.</summary>
    public bool HasPromptBelow => _hasPromptBelow;

    /// <summary>Raised when <see cref="AtBottom"/>, <see cref="AtTop"/>, <see cref="HasPromptAbove"/> or
    /// <see cref="HasPromptBelow"/> changes.</summary>
    public event Action? ScrollStateChanged;

    // "Claude is working" indicator: an avatar-aligned bubble of bouncing dots + the current action, kept as
    // the last child of _stack while a turn runs (and not paused on a permission). Reused across shows.
    private readonly Control _activityRow;
    private readonly TextBlock _activityLabel;
    private bool _activityShown;

    /// <summary>The user answered a permission card: (item, allow, also switch to the suggested mode).</summary>
    public event Action<PermissionItem, bool, bool>? PermissionAnswered;

    /// <summary>The user answered an AskUserQuestion card: question text → chosen labels.</summary>
    public event Action<PermissionItem, IReadOnlyDictionary<string, IReadOnlyList<string>>>? QuestionAnswered;

    /// <summary>A file reference (a tool card's filename) was picked to open in the Markdown viewer — the
    /// absolute path. Routed out because the app owns the viewer window.</summary>
    public event Action<string>? OpenFileRequested;

    /// <summary>A file reference's "View diff" was picked — the absolute path (opens the git tree on it).</summary>
    public event Action<string>? ViewDiffRequested;

    /// <summary>The session's working directory, used to resolve a tool's file path to an absolute one. Set
    /// before <see cref="Bind"/> so tool cards built during materialisation can arm their file references.</summary>
    public string Cwd { get; set; } = "";

    // A subclass keeps the base control's template/styles only if it says so — without this the
    // ScrollViewer has no presenter, so nothing scrolls and no bar appears.
    protected override Type StyleKeyOverride => typeof(ScrollViewer);

    private sealed class ItemView
    {
        public required Control Root;
        public StackPanel? Body;                                     // assistant: the parts column
        public readonly Dictionary<AssistantPart, Control> Parts = new();
        public readonly Dictionary<ToolCallPart, ToolCard> Tools = new();
    }

    public SessionThreadView(SessionPalette palette)
    {
        _p = palette;
        _initials = Initials(Environment.UserName);
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        Background = _p.Surface;

        _stack = new StackPanel
        {
            Spacing = 22,
            MaxWidth = SessionPalette.ThreadMaxWidth + 44,
            Margin = new Thickness(22, 26, 22, 20),
            HorizontalAlignment = HorizontalAlignment.Stretch,   // + MaxWidth → centred column
        };
        // The find-match overlay sits above the thread in the same scrolled coordinate space, so its
        // translucent rectangles ride along as the content scrolls. Click-through so it never eats selection.
        _highlightLayer = new Canvas { IsHitTestVisible = false };
        Content = new Panel { Children = { _stack, _highlightLayer } };

        // Follow the tail only while the user is at (or near) the bottom; scrolling up pins the view. Only a
        // change the *user* made (offset moved, extent unchanged) re-evaluates — content growing pushes the
        // bottom away before we've scrolled to it, and must not be read as "the user scrolled up".
        ScrollChanged += (_, e) =>
        {
            RecomputeScrollState();   // keep the floating jump buttons in sync with every scroll/growth
            // A reflow (extent changed — expand/collapse, streaming) moves match rectangles; repaint them.
            if (e.ExtentDelta.Y != 0) { if (_matches.Count > 0) RefreshHighlightLayer(); return; }
            if (e.OffsetDelta.Y == 0) return;
            _stickToBottom = Offset.Y + Viewport.Height >= Extent.Height - 24;
        };
        // A width change reflows text (wraps differently) → recompute match rectangles.
        SizeChanged += (_, _) => { if (_matches.Count > 0) RefreshHighlightLayer(); };

        // The working indicator (built once, added/removed from the column as turns come and go).
        _activityLabel = new TextBlock
        {
            FontSize = 13.5, FontFamily = _p.Body, FontWeight = FontWeight.SemiBold, Foreground = _p.Muted,
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var activityBubble = new Border
        {
            Background = _p.Raised, BorderBrush = _p.BorderSoft, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5, 16, 16, 16), Padding = new Thickness(15, 10),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TypingDots { Fill = _p.Brand, VerticalAlignment = VerticalAlignment.Center },
                    _activityLabel,
                },
            },
        };
        _activityRow = Row(activityBubble, left: Avatar(), right: null);
    }

    /// <summary>Points the view at a conversation (materialising what it already holds) and follows it.</summary>
    public void Bind(SessionConversation conversation)
    {
        if (_conv is not null)
        {
            _conv.Changed -= OnChanged;
            _conv.Reset -= OnReset;
            _conv.StateChanged -= OnStateChanged;
        }
        _conv = conversation;
        _activityShown = false;   // the row was cleared with the column; re-add it below if the turn is live
        _userRows.Clear();
        _stack.Children.Clear();
        _views.Clear();
        _wraps.Clear();
        _matches.Clear();
        _matchCurrent = -1;
        _highlightLayer.Children.Clear();
        _compactions.Clear();
        foreach (var item in conversation.Items) AddItem(item);
        conversation.Changed += OnChanged;
        conversation.Reset += OnReset;
        conversation.StateChanged += OnStateChanged;
        UpdateActivity();
        _stickToBottom = true;
        ScrollToEndSoon();
    }

    // History landed in front of the live items: rebuild the whole column (cheap — a few hundred controls).
    private void OnReset()
    {
        if (_conv is { } c) Bind(c);
    }

    /// <summary>Re-tint after a live theme swap. The palette's brushes are re-coloured in place (so chrome and
    /// text follow automatically), but each item's markdown baked its code-syntax colours for the old light/
    /// dark side at build time — rebuild the column so it re-picks them. Cheap; a no-op when unbound.</summary>
    public void Restyle()
    {
        Background = _p.Surface;
        if (_conv is { } c) Bind(c);
    }

    private void OnChanged(ConversationItem item, ConversationChange change)
    {
        if (change == ConversationChange.Added) AddItem(item);
        else UpdateItem(item);
        UpdateActivity();   // a tool flipping status (or a new part) changes what the indicator says
        if (_stickToBottom) ScrollToEndSoon();
    }

    // Turn-level state moved (a turn opened/closed, a permission became pending): show or hide the indicator.
    private void OnStateChanged()
    {
        UpdateActivity();
        if (_stickToBottom) ScrollToEndSoon();
    }

    // Shows the "working" bubble as the last row while a turn runs and isn't paused on a permission; keeps it
    // pinned to the tail as later items append, and pulls it once the turn settles.
    private void UpdateActivity()
    {
        bool show = _conv is { TurnActive: true, PendingPermission: null };
        if (show)
        {
            _activityLabel.Text = ActivityLabel();
            if (!_activityShown)
            {
                _stack.Children.Add(_activityRow);
                _activityShown = true;
            }
            else if (_stack.Children.Count > 0 && !ReferenceEquals(_stack.Children[^1], _activityRow))
            {
                // An item appended after it — move the indicator back to the tail.
                _stack.Children.Remove(_activityRow);
                _stack.Children.Add(_activityRow);
            }
        }
        else if (_activityShown)
        {
            _stack.Children.Remove(_activityRow);
            _activityShown = false;
        }
    }

    // What the indicator says: the concrete running tool (its summary), else the phase of the open turn.
    private string ActivityLabel()
    {
        if (_conv?.Items is { Count: > 0 } items && items[^1] is AssistantMessageItem { Parts.Count: > 0 } a)
            return a.Parts[^1] switch
            {
                ToolCallPart { Status: ToolCallStatus.Running } tool => tool.Summary is { Length: > 0 } s ? s : $"Running {tool.ToolName}",
                ThinkingPart                                         => "Thinking…",
                TextPart { IsStreaming: true }                       => "Responding…",
                _                                                    => "Working…",
            };
        return "Working…";
    }

    private void ScrollToEndSoon() =>
        Dispatcher.UIThread.Post(() => { if (_stickToBottom) ScrollToEnd(); }, DispatcherPriority.Background);

    // ── Jump buttons (docs/session-composer-enhancements-plan.md §4) ──────────────

    /// <summary>Scroll to the tail and resume following it (the "jump to bottom" button).</summary>
    public void JumpToBottom()
    {
        _stickToBottom = true;
        ScrollToEnd();
        RecomputeScrollState();
    }

    /// <summary>Scroll to the very top of the thread (the "jump to top" button).</summary>
    public void JumpToTop()
    {
        _stickToBottom = false;
        Offset = new Vector(Offset.X, 0);
        RecomputeScrollState();
    }

    /// <summary>Scroll to the nearest user prompt below the current viewport top, placing it near the top — so
    /// repeated clicks walk downward through later prompts. No-op when nothing is below.</summary>
    public void JumpToNextPrompt()
    {
        // The closest prompt below = the one with the least content-top still below the viewport top.
        double bestTop = double.PositiveInfinity;
        foreach (var row in _userRows)
        {
            if (row.TranslatePoint(new Point(0, 0), this) is not { } p) continue;
            if (p.Y <= PromptAboveEpsilon) continue;             // at/above the viewport top — not "below"
            double top = Offset.Y + p.Y;                          // p is viewport-relative; +Offset → content Y
            if (top < bestTop) bestTop = top;
        }
        if (double.IsPositiveInfinity(bestTop)) return;
        double max = Math.Max(0, Extent.Height - Viewport.Height);
        Offset = new Vector(Offset.X, Math.Clamp(bestTop - 12, 0, max));
        _stickToBottom = false;
        RecomputeScrollState();
    }

    /// <summary>Scroll to the nearest user prompt above the current viewport top, placing it near the top — so
    /// repeated clicks walk upward through earlier prompts. No-op when nothing is above.</summary>
    public void JumpToPreviousPrompt()
    {
        // The closest prompt above = the one with the greatest content-top still above the viewport top.
        double bestTop = double.NegativeInfinity;
        foreach (var row in _userRows)
        {
            if (row.TranslatePoint(new Point(0, 0), this) is not { } p) continue;
            if (p.Y >= -PromptAboveEpsilon) continue;             // at/below the viewport top — not "above"
            double top = Offset.Y + p.Y;                          // p is viewport-relative; +Offset → content Y
            if (top > bestTop) bestTop = top;
        }
        if (double.IsNegativeInfinity(bestTop)) return;
        double max = Math.Max(0, Extent.Height - Viewport.Height);
        Offset = new Vector(Offset.X, Math.Clamp(bestTop - 12, 0, max));
        _stickToBottom = false;
        RecomputeScrollState();
    }

    // Recompute whether the tail is in view and whether any prompt sits above it, and notify the window when
    // either flips.
    private void RecomputeScrollState()
    {
        bool atBottom = Offset.Y + Viewport.Height >= Extent.Height - 24;
        bool atTop = Offset.Y <= 8;
        bool above = false, below = false;
        if (Viewport.Height > 0)
            foreach (var row in _userRows)
                if (row.TranslatePoint(new Point(0, 0), this) is { } p)
                {
                    if (p.Y < -PromptAboveEpsilon) above = true;
                    else if (p.Y > PromptAboveEpsilon) below = true;
                }

        if (atBottom == _atBottom && atTop == _atTop && above == _hasPromptAbove && below == _hasPromptBelow) return;
        _atBottom = atBottom;
        _atTop = atTop;
        _hasPromptAbove = above;
        _hasPromptBelow = below;
        ScrollStateChanged?.Invoke();
    }

    // ── Items ────────────────────────────────────────────────────────────────────

    private void AddItem(ConversationItem item)
    {
        ItemView view = item switch
        {
            UserMessageItem u      => new ItemView { Root = BuildUser(u) },
            AssistantMessageItem a => BuildAssistant(a),
            PermissionItem p       => new ItemView { Root = Row(BuildPermission(p), null, null) },
            NoteItem n             => new ItemView { Root = Row(BuildNote(n), null, null) },
            CompactionItem cm      => new ItemView { Root = Row(BuildCompaction(cm), null, null) },
            _                      => new ItemView { Root = new Panel() },
        };
        _views[item] = view;
        // Every item sits in a highlight wrapper (inert until Ctrl+F lights it up). The 1px transparent border
        // is always present so toggling the outline on a match never shifts the layout.
        var wrap = new Border
        {
            Child = view.Root, CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1), BorderBrush = Brushes.Transparent, Background = Brushes.Transparent,
        };
        _wraps[item] = wrap;
        _stack.Children.Add(wrap);
        if (item is UserMessageItem) _userRows.Add(view.Root);
        Dispatcher.UIThread.Post(RecomputeScrollState, DispatcherPriority.Background);
    }

    private void UpdateItem(ConversationItem item)
    {
        if (!_views.TryGetValue(item, out var view)) return;
        switch (item)
        {
            case AssistantMessageItem a:
                SyncParts(view, a);
                break;
            case PermissionItem p:
                // A resolved card becomes a compact receipt: rebuild and swap inside the item's wrapper.
                var fresh = Row(BuildPermission(p), null, null);
                if (_wraps.TryGetValue(item, out var wrap)) wrap.Child = fresh;
                view.Root = fresh;
                break;
            case CompactionItem cm:
                if (_compactions.TryGetValue(cm, out var card)) card.Update(cm);
                break;
        }
    }

    // ── Ctrl+F find ────────────────────────────────────────────────────────────────

    /// <summary>Number of occurrences from the last <see cref="Search"/> (a collapsed section counts once).</summary>
    public int MatchCount => _matches.Count;

    /// <summary>Index of the shown match, or -1 when none is current.</summary>
    public int CurrentMatch => _matchCurrent;

    /// <summary>Recompute every occurrence of <paramref name="query"/> in the thread (case-insensitive),
    /// walking the actual rendered text controls so matches are per-occurrence, not per-message. Returns the
    /// count. Text that's currently collapsed (a hidden thinking/tool body) collapses to a single hit anchored
    /// to its visible card, so stepping doesn't sit on one collapsed section once per hidden value inside it.</summary>
    public int Search(string query)
    {
        ClearSearch();
        if (_conv is null || string.IsNullOrWhiteSpace(query)) return 0;

        foreach (var item in _conv.Items)
        {
            if (!_wraps.TryGetValue(item, out var wrap)) continue;
            var collapsedSeen = new HashSet<Control>();   // dedup: one hit per collapsed section within this item
            foreach (var tb in wrap.Child?.GetVisualDescendants().OfType<TextBlock>() ?? [])
            {
                var text = BlockText(tb);
                if (string.IsNullOrEmpty(text) || text.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                if (!tb.IsEffectivelyVisible)
                {
                    // Collapsed: one hit for the whole hidden section, lighting/scrolling its visible card.
                    var anchor = NearestVisibleAncestor(tb) ?? wrap;
                    if (collapsedSeen.Add(anchor))
                        _matches.Add(new FindMatch { Anchor = anchor, CardOnly = true });
                    continue;
                }

                // A visible occurrence: one hit per position, lit by its exact glyph rectangles.
                for (int i = text.IndexOf(query, StringComparison.OrdinalIgnoreCase); i >= 0;
                     i = text.IndexOf(query, i + Math.Max(1, query.Length), StringComparison.OrdinalIgnoreCase))
                    _matches.Add(new FindMatch { Anchor = tb, Text = tb, Start = i, Length = query.Length });
            }
        }
        RefreshHighlightLayer();
        return _matches.Count;
    }

    /// <summary>Make the match at <paramref name="index"/> (wrapped into range) current — repaint the overlay
    /// so it reads brighter than the rest — and scroll it into view. A no-op when there are no matches.</summary>
    public void ShowMatch(int index)
    {
        if (_matches.Count == 0) return;
        int n = _matches.Count;
        _matchCurrent = ((index % n) + n) % n;
        RefreshHighlightLayer();
        _matches[_matchCurrent].Anchor.BringIntoView();   // the scroll handler recomputes tail-follow
    }

    /// <summary>Clear all find highlighting and match state (find bar closed, or the query emptied).</summary>
    public void ClearSearch()
    {
        _highlightLayer.Children.Clear();
        _matches.Clear();
        _matchCurrent = -1;
    }

    // Repaint the overlay: every match dim, the current one brighter. Rectangles are computed from each match's
    // glyph geometry (or its collapsed card's bounds) and placed in the layer's coordinate space, which is the
    // scrolled content's — so they track the text as it scrolls. Cheap; called on search, navigation and reflow.
    private void RefreshHighlightLayer()
    {
        _highlightLayer.Children.Clear();
        if (_matches.Count == 0) return;

        var dim = new SolidColorBrush(Tint(0x2E));
        var cur = new SolidColorBrush(Tint(0x66));
        for (int i = 0; i < _matches.Count; i++)
        {
            var brush = i == _matchCurrent ? cur : dim;
            foreach (var r in RectsFor(_matches[i]))
            {
                var box = new Border { Background = brush, CornerRadius = new CornerRadius(2), IsHitTestVisible = false };
                Canvas.SetLeft(box, r.X);
                Canvas.SetTop(box, r.Y);
                box.Width = r.Width;
                box.Height = r.Height;
                _highlightLayer.Children.Add(box);
            }
        }
    }

    private Color Tint(byte alpha) { var c = _p.Brand.Color; return Color.FromArgb(alpha, c.R, c.G, c.B); }

    // A match's rectangle(s) in the highlight layer's space: a visible occurrence yields its exact glyph run(s)
    // from the text layout; a collapsed/card hit yields the whole card's bounds. Empty when the control isn't
    // laid out yet or can't be mapped into the layer.
    private IEnumerable<Rect> RectsFor(FindMatch m)
    {
        if (!m.CardOnly && m.Text is { } tb && tb.IsEffectivelyVisible)
        {
            var pad = tb.Padding;
            foreach (var r in HitRanges(tb, m.Start, m.Length))
                if (tb.TranslatePoint(new Point(r.X + pad.Left, r.Y + pad.Top), _highlightLayer) is { } tl)
                    yield return new Rect(tl.X, tl.Y, r.Width, r.Height);
            yield break;
        }

        if (m.Anchor.Bounds is { Width: > 0, Height: > 0 } b
            && m.Anchor.TranslatePoint(new Point(0, 0), _highlightLayer) is { } origin)
            yield return new Rect(origin.X, origin.Y, b.Width, b.Height);
    }

    // The glyph rectangles of [start, start+length) in a text block, or empty if the layout can't map them.
    private static IReadOnlyList<Rect> HitRanges(TextBlock tb, int start, int length)
    {
        try { return tb.TextLayout.HitTestTextRange(start, length).ToList(); }
        catch { return []; }
    }

    // The nearest ancestor of a hidden control that is itself effectively visible — the collapsed section's
    // visible card, which is what we light and scroll to for a collapsed match.
    private static Control? NearestVisibleAncestor(Visual v) =>
        v.GetVisualAncestors().OfType<Control>().FirstOrDefault(c => c.IsEffectivelyVisible);

    // A text block's plain text for searching AND for selection offsets. Markdown prose/code are built from
    // inline Runs, so their TextBlock.Text is empty — fall back to the inlines' concatenated text, which is
    // exactly the character stream the TextLayout (and thus SelectionStart/SelectionEnd) indexes into.
    private static string BlockText(TextBlock tb)
    {
        if (!string.IsNullOrEmpty(tb.Text)) return tb.Text;
        if (tb.Inlines is not { Count: > 0 } inlines) return "";
        var sb = new System.Text.StringBuilder();
        AppendInlineText(inlines, sb);
        return sb.ToString();
    }

    private static void AppendInlineText(InlineCollection inlines, System.Text.StringBuilder sb)
    {
        foreach (var inline in inlines)
            switch (inline)
            {
                case Run r: sb.Append(r.Text); break;
                case LineBreak: sb.Append('\n'); break;
                case Span s when s.Inlines is { } inner: AppendInlineText(inner, sb); break;
            }
    }

    // ── User ─────────────────────────────────────────────────────────────────────

    private Control BuildUser(UserMessageItem u)
    {
        // A slash command reads as a command, not prose — render it as a compact mono chip rather than a bubble.
        Control bubble = SlashCommandCatalog.LooksLikeCommand(u.Text)
            ? new Border
            {
                Background = _p.Raised2, BorderBrush = _p.BrandLine, BorderThickness = new Thickness(1),
                CornerRadius = SessionPalette.PillRadius, Padding = new Thickness(13, 8),
                MaxWidth = SessionPalette.ThreadMaxWidth * 0.76, HorizontalAlignment = HorizontalAlignment.Right,
                Child = new SelectableTextBlock
                {
                    Text = u.Text.Trim(), FontSize = 13, FontFamily = _p.Mono, Foreground = _p.Brand,
                    TextWrapping = TextWrapping.Wrap,
                },
            }
            : BubbleForText(u.Text, u.Attachments);
        var who = new Border
        {
            Width = 29, Height = 29, CornerRadius = new CornerRadius(9), Background = _p.Brand,
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock
            {
                Text = _initials, FontFamily = _p.Display, FontWeight = FontWeight.Bold, FontSize = 12,
                Foreground = _p.BrandInk, HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        bubble.HorizontalAlignment = HorizontalAlignment.Right;

        // Attachments (dropped/pasted files + images) ride under the bubble as chips, right-aligned to match.
        // An image-only message (e.g. a resumed "[Image #1]" whose text stripped to nothing) shows just the
        // chips — no empty bubble above them.
        bool hasText = !string.IsNullOrWhiteSpace(u.Text);
        Control middle = bubble;
        if (u.Attachments.Count > 0)
        {
            var tray = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
            foreach (var a in u.Attachments) tray.Children.Add(new AttachmentChip(_p, a));
            middle = hasText
                ? new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, Children = { bubble, tray } }
                : tray;
        }
        return Row(WithCopy(middle, () => u.Text, userSide: true), left: null, right: who);
    }

    // Wraps a message bubble so a hover-revealed "copy" button floats at its interior top corner (the user's
    // is top-left, Claude's top-right, each away from that side's avatar). The text is read lazily on click,
    // so a still-streaming assistant bubble copies whatever it holds at that moment.
    private Control WithCopy(Control content, Func<string?> text, bool userSide)
    {
        var btn = new CopyButton(_p, text)
        {
            HorizontalAlignment = userSide ? HorizontalAlignment.Left : HorizontalAlignment.Right,
            Margin = userSide ? new Thickness(9, 9, 0, 0) : new Thickness(0, 9, 9, 0),
        };
        var grid = new Grid
        {
            Background = Brushes.Transparent,
            HorizontalAlignment = userSide ? HorizontalAlignment.Right : HorizontalAlignment.Stretch,
        };
        grid.Children.Add(content);
        grid.Children.Add(btn);
        grid.PointerEntered += (_, _) => btn.Reveal(true);
        grid.PointerExited += (_, _) => btn.Reveal(false);
        return grid;
    }

    // The plain text of an assistant turn (its rendered prose blocks, joined) for the copy button.
    private static string AssistantPlainText(AssistantMessageItem a) =>
        string.Join("\n\n", a.Parts.OfType<TextPart>().Select(p => p.Text).Where(t => !string.IsNullOrWhiteSpace(t)));

    // The user's warm bubble for a plain text message, with any URLs made clickable and any "[Image #N]"
    // placeholder (a resumed message that carried an image) made hover-preview + click-to-open.
    private Border BubbleForText(string text, IReadOnlyList<MessageAttachment>? attachments = null)
    {
        var prose = new SelectableTextBlock
        {
            Text = text, FontSize = SessionPalette.ProseSize, FontFamily = _p.Body, Foreground = _p.Title,
            TextWrapping = TextWrapping.Wrap, LineHeight = SessionPalette.ProseSize * 1.5,
        };
        LinkText.AttachDetected(prose, text);
        // The popup ImageRefText floats needs a rooted parent, so the prose lives inside a host panel.
        var host = new Panel { Children = { prose } };
        if (attachments is { Count: > 0 })
            ImageRefText.Attach(prose, host, text, attachments, _p);
        return new Border
        {
            Background = _p.BrandWash, BorderBrush = _p.BrandLine, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16, 16, 5, 16), Padding = new Thickness(15, 11),
            MaxWidth = SessionPalette.ThreadMaxWidth * 0.76, Child = host,
        };
    }

    // Every row shares one three-column layout: a left gutter for Claude's mark, the content column, and a
    // right gutter for the user's head. Bubbles and cards live only in the middle, so a head never sits over
    // (or lines up with the edge of) the other side's content.
    private const double GutterWidth = 29, GutterGap = 12;

    private static Control Row(Control middle, Control? left, Control? right)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions($"{GutterWidth},{GutterGap},*,{GutterGap},{GutterWidth}") };
        if (left is not null) grid.Children.Add(left);
        Grid.SetColumn(middle, 2);
        grid.Children.Add(middle);
        if (right is not null)
        {
            Grid.SetColumn(right, 4);
            grid.Children.Add(right);
        }
        return grid;
    }

    // ── Assistant ────────────────────────────────────────────────────────────────

    // The bird-mark avatar for Claude's side (assistant turns and the working indicator).
    private Control Avatar() => new Border
    {
        Width = 29, Height = 29, CornerRadius = new CornerRadius(9), Background = _p.Raised2,
        BorderBrush = _p.Border, BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Top,
        Child = MarkImage(19),
    };

    private ItemView BuildAssistant(AssistantMessageItem a)
    {
        var avatar = Avatar();
        var body = new StackPanel();
        // Claude's turn in a bubble too (raised, soft-edged, tail toward the avatar): a bounded surface is
        // easier on the eye than open prose over a long thread. Parts carry their own bottom margins, so the
        // bubble's bottom padding is trimmed to keep the last one from double-spacing.
        var bubble = new Border
        {
            Background = _p.Raised, BorderBrush = _p.BorderSoft, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5, 16, 16, 16), Padding = new Thickness(15, 12, 15, 2),
            Child = body,
        };
        var view = new ItemView { Root = Row(WithCopy(bubble, () => AssistantPlainText(a), userSide: false), left: avatar, right: null), Body = body };
        SyncParts(view, a);
        return view;
    }

    // Append the parts the view hasn't seen; update the ones it has (streaming text, tool status).
    private void SyncParts(ItemView view, AssistantMessageItem a)
    {
        if (view.Body is not { } body) return;
        foreach (var part in a.Parts)
        {
            if (view.Parts.TryGetValue(part, out var existing))
            {
                switch (part)
                {
                    case TextPart t when t.IsStreaming && existing is SelectableTextBlock stb:
                        stb.Text = t.Text;
                        break;
                    case TextPart t when !t.IsStreaming && existing is SelectableTextBlock:
                        // Finalised: the plain accumulator gives way to the rich Markdown render.
                        var rendered = Prose(t.Text);
                        int at = body.Children.IndexOf(existing);
                        if (at >= 0) body.Children[at] = rendered; else body.Children.Add(rendered);
                        view.Parts[part] = rendered;
                        break;
                    case ToolCallPart tool when view.Tools.TryGetValue(tool, out var card):
                        card.Update(tool);
                        break;
                }
                continue;
            }

            Control c;
            switch (part)
            {
                case TextPart t:
                    c = t.IsStreaming ? StreamingText(t.Text) : Prose(t.Text);
                    break;
                case ThinkingPart th:
                    c = BuildThinking(th);
                    break;
                case ToolCallPart tool:
                    var card = new ToolCard(_p, tool, Cwd,
                        path => OpenFileRequested?.Invoke(path), path => ViewDiffRequested?.Invoke(path));
                    view.Tools[tool] = card;
                    c = card.Root;
                    break;
                default:
                    continue;
            }
            view.Parts[part] = c;
            body.Children.Add(c);
        }
    }

    private SelectableTextBlock StreamingText(string text) => new()
    {
        Text = text, FontSize = SessionPalette.ProseSize, FontFamily = _p.Body, Foreground = _p.Text,
        TextWrapping = TextWrapping.Wrap, LineHeight = SessionPalette.ProseSize * 1.62,
        Margin = new Thickness(0, 0, 0, 11),
    };

    // Prose renders through the shared Markdown view; inline-code file references become clickable (Ctrl+click
    // a Markdown file to view, right-click any file for view/diff/reveal/editor) when they resolve on disk.
    private Control Prose(string md) => MarkdownView.Build(md, _p.Prose,
        new MarkdownView.FileRefContext(Cwd, p => OpenFileRequested?.Invoke(p), p => ViewDiffRequested?.Invoke(p)));

    // Collapsible thinking disclosure: a one-line summary, the full thought on click.
    private Control BuildThinking(ThinkingPart th)
    {
        var chevron = new TextBlock { Text = "▸", Foreground = _p.Faint, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        var header = new Border
        {
            Padding = new Thickness(13, 9), Cursor = new Cursor(StandardCursorType.Hand), Background = Brushes.Transparent,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 9,
                Children =
                {
                    chevron,
                    new TextBlock
                    {
                        Text = "Thought · " + Gist(th.Text), Foreground = _p.Muted, FontSize = 13,
                        FontWeight = FontWeight.SemiBold, FontFamily = _p.Body, TextTrimming = TextTrimming.CharacterEllipsis,
                    },
                },
            },
        };
        var bodyText = new Border
        {
            Padding = new Thickness(34, 2, 15, 13), IsVisible = false,
            Child = new SelectableTextBlock
            {
                Text = th.Text, FontStyle = FontStyle.Italic, Foreground = _p.Muted, FontSize = 13.5,
                FontFamily = _p.Body, TextWrapping = TextWrapping.Wrap, LineHeight = 13.5 * 1.6,
            },
        };
        header.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton != MouseButton.Left) return;
            bodyText.IsVisible = !bodyText.IsVisible;
            chevron.Text = bodyText.IsVisible ? "▾" : "▸";
        };
        return new Border
        {
            BorderBrush = _p.Border, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(11),
            Background = _p.Surface, Margin = new Thickness(0, 0, 0, 14),
            Child = new StackPanel { Children = { header, bodyText } },
        };
    }

    // ── Tool card ────────────────────────────────────────────────────────────────

    /// <summary>A tool call as a card: icon + "Verb <em>object</em>" + live status, with the result (and, when
    /// expanded by click, the arguments) in a mono panel beneath.</summary>
    private sealed class ToolCard
    {
        private readonly SessionPalette _p;
        private readonly Ellipse _dot;
        private readonly TextBlock _status;
        private readonly Border _out;
        private readonly StackPanel _bodyStack;
        // For an Edit/MultiEdit/Write tool, the unified line diff of its input (built once) so the card shows a
        // terminal-style +/- diff instead of a raw JSON dump; null for every other tool.
        private readonly IReadOnlyList<GitDiffLine>? _diffLines;
        // A card toggles only when it has something worth showing: an edit's diff (the formatted detail), the
        // full result text when that's more than the one-line collapsed preview, or — for a tool we don't know
        // how to format — its raw input. Recomputed in Update because the result arrives after construction.
        private bool _expandable;
        private readonly TextBlock _chevron;   // ▸/▾ affordance in the header, shown only when expandable
        private readonly Border _headerBorder;
        private Control? _diffPanel;   // the rendered diff, built lazily and reused across status updates
        private ToolCallPart _part;
        private bool _expanded;

        public Border Root { get; }

        public ToolCard(SessionPalette p, ToolCallPart part, string cwd,
            Action<string> openFile, Action<string> viewDiff)
        {
            _p = p;
            _part = part;

            _diffLines = EditDiff.Build(part.ToolName, ParseOrNull(part.InputJson));
            _expanded = _diffLines is { Count: > 0 };   // an edit's diff shows by default; other detail stays closed

            var icon = new Border
            {
                Width = 26, Height = 26, CornerRadius = new CornerRadius(8), Background = p.Raised2,
                Child = new TextBlock
                {
                    Text = Glyph(part.ToolName), Foreground = p.Muted, FontSize = 13,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                },
            };
            var (verb, obj) = SplitSummary(part.Summary);
            // A file tool's object is a real file: render the filename as its own interactive token (left-click
            // opens a Markdown file in the viewer; right-click offers view/diff/reveal/editor) instead of a plain
            // inline run. Everything else keeps the two-run inline summary.
            Control summary;
            if (FilePathOf(part, cwd) is { } filePath)
            {
                var name = new TextBlock
                {
                    Text = obj.Length > 0 ? obj : PathLeaf.Of(filePath), Foreground = p.Muted, FontSize = 13,
                    FontFamily = p.Mono, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
                };
                FileRef.Attach(name, filePath, openFile, viewDiff,
                    FileRef.IsMarkdown(filePath) ? () => openFile(filePath) : null);
                summary = new StackPanel
                {
                    Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = verb + " ", Foreground = p.Title, FontWeight = FontWeight.SemiBold, FontSize = 14,
                            FontFamily = p.Body, VerticalAlignment = VerticalAlignment.Center,
                        },
                        name,
                    },
                };
            }
            else
            {
                summary = new TextBlock
                {
                    TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
                    Inlines = new InlineCollection
                    {
                        new Run(verb + " ") { Foreground = p.Title, FontWeight = FontWeight.SemiBold, FontSize = 14, FontFamily = p.Body },
                        new Run(obj) { Foreground = p.Muted, FontSize = 13, FontFamily = p.Mono },
                    },
                };
            }
            _dot = new Ellipse { Width = 7, Height = 7, VerticalAlignment = VerticalAlignment.Center };
            _status = new TextBlock { FontSize = 12, FontWeight = FontWeight.SemiBold, FontFamily = p.Mono, VerticalAlignment = VerticalAlignment.Center };
            _chevron = new TextBlock
            {
                FontSize = 11, Foreground = p.Faint, VerticalAlignment = VerticalAlignment.Center, IsVisible = false,
            };
            var status = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center,
                Children = { _dot, _status, _chevron },
            };

            var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,11,*,11,Auto") };
            header.Children.Add(icon);
            Grid.SetColumn(summary, 2);
            header.Children.Add(summary);
            Grid.SetColumn(status, 4);
            header.Children.Add(status);
            _headerBorder = new Border
            {
                Padding = new Thickness(13, 10), Child = header, Background = Brushes.Transparent,
            };
            _headerBorder.PointerReleased += (_, e) =>
            {
                if (e.InitialPressMouseButton != MouseButton.Left || !_expandable) return;
                _expanded = !_expanded;
                Update(_part);
            };

            _bodyStack = new StackPanel { Spacing = 10 };
            _out = new Border
            {
                BorderBrush = p.BorderSoft, BorderThickness = new Thickness(0, 1, 0, 0), Background = p.CodeBg,
                Padding = new Thickness(14, 11), IsVisible = false, Child = _bodyStack,
            };

            Root = new Border
            {
                BorderBrush = p.Border, BorderThickness = new Thickness(1), CornerRadius = SessionPalette.CardRadius,
                Background = p.Surface, ClipToBounds = true, Margin = new Thickness(0, 0, 0, 12),
                Child = new StackPanel { Children = { _headerBorder, _out } },
            };
            Update(part);
        }

        public void Update(ToolCallPart part)
        {
            _part = part;
            var (brush, label) = part.Status switch
            {
                ToolCallStatus.Done   => (_p.Ok, "done"),
                ToolCallStatus.Failed => (_p.Err, "failed"),
                _                     => (_p.Await, "running"),
            };
            _dot.Fill = brush;
            _status.Foreground = brush;
            _status.Text = label;

            bool failed = part.Status == ToolCallStatus.Failed;
            bool hasDiff = _diffLines is { Count: > 0 };
            bool known = ToolSummary.IsKnown(part.ToolName);
            var input = ParseOrNull(part.InputJson);
            string full = part.ResultText;
            // The collapsed one-liner: a per-tool summary where a count reads better ("Read 42 lines",
            // "12 files"), else the first line of the output.
            string collapsed = ToolResultFormat.CollapsedSummary(part.ToolName, input, full) ?? ShortLine(full);
            // Bash/PowerShell clip their command in the header; keep the full one to show (and to make the card
            // expandable) when it was clipped.
            string? command = ToolResultFormat.Command(part.ToolName, input);

            // A card toggles when it has a diff, more result than the collapsed line conveys, a long command to
            // reveal, or (unknown tool) a raw input we couldn't format. Recomputed here because the result
            // lands after the card is first built.
            bool moreResult = full.Length > 0 && collapsed != full;
            _expandable = hasDiff || moreResult || !known || command is { Length: > 60 };
            _chevron.IsVisible = _expandable;
            _chevron.Text = _expanded ? "▾" : "▸";
            _headerBorder.Cursor = _expandable ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
            bool showExpanded = _expandable && _expanded;
            _bodyStack.Children.Clear();

            if (hasDiff)
            {
                // An edit tool: the +/- diff is the formatted detail the toggle shows/hides (never raw JSON).
                // A failed edit also surfaces its error, so the reason is visible even when the diff is closed.
                if (showExpanded) _bodyStack.Children.Add(_diffPanel ??= BuildDiffPanel(_diffLines!));
                if (failed && full.Length > 0) _bodyStack.Children.Add(MonoText(full, true));
            }
            else if (showExpanded)
            {
                // Expanded: the full result output in a height-capped, scrollable mono panel, headed by the full
                // shell command for Bash/PowerShell (its header clips it). Only an *unknown* tool — one we can't
                // format — shows its raw input instead; a known tool never dumps JSON.
                if (!known)
                    _bodyStack.Children.Add(MonoText(PrettyJson(part.InputJson), false));
                else if (command is { Length: > 0 })
                    _bodyStack.Children.Add(MonoText((part.ToolName == "PowerShell" ? "PS> " : "$ ") + command, false));
                if (full.Length > 0) _bodyStack.Children.Add(BuildOutputPanel(full, failed));
            }
            else if (collapsed.Length > 0)
            {
                // Collapsed: the per-tool one-line summary.
                _bodyStack.Children.Add(MonoText(collapsed, failed));
            }
            _out.IsVisible = _bodyStack.Children.Count > 0;
        }

        // A single-line summary of a tool result for the collapsed card: its first non-blank line, clipped.
        private static string ShortLine(string full)
        {
            if (full.Length == 0) return "";
            int nl = full.IndexOf('\n');
            var first = (nl >= 0 ? full[..nl] : full).Trim();
            const int max = 100;
            return first.Length <= max ? first : first[..max].TrimEnd() + "…";
        }

        // The full tool output, expanded: a selectable mono block (URLs clickable) inside a height-capped
        // scroll region, so a big Read/Bash result reads in full without pushing the rest of the thread away.
        private Control BuildOutputPanel(string full, bool failed) => new ScrollViewer
        {
            MaxHeight = 320, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = MonoText(full, failed),
        };

        // A mono result/argument block (URLs made clickable), coloured for a failed tool.
        private Control MonoText(string text, bool failed)
        {
            var tb = new SelectableTextBlock
            {
                Text = text, FontFamily = _p.Mono, FontSize = 12, LineHeight = 12 * 1.65,
                Foreground = failed ? _p.Err : _p.Muted, TextWrapping = TextWrapping.Wrap,
            };
            LinkText.AttachDetected(tb, text);   // any URLs in tool output become clickable
            return tb;
        }

        // Renders a unified line diff as a terminal-style panel: a "+N -M" summary, then one row per line —
        // a non-selectable +/- marker column beside the (selectable) code, with a faint band tinting changed
        // rows. The delta hues mirror DiffView so the session UI and the git tree colour edits identically.
        private Control BuildDiffPanel(IReadOnlyList<GitDiffLine> lines)
        {
            var (addFg, remFg, addBand, remBand) = _p.IsDark
                ? (Color.FromRgb(0x3F, 0xB9, 0x50), Color.FromRgb(0xF8, 0x51, 0x49),
                   Color.FromArgb(36, 0x3F, 0xB9, 0x50), Color.FromArgb(36, 0xF8, 0x51, 0x49))
                : (Color.FromRgb(0x1F, 0x88, 0x3D), Color.FromRgb(0xCF, 0x22, 0x2E),
                   Color.FromArgb(30, 0x1F, 0x88, 0x3D), Color.FromArgb(28, 0xCF, 0x22, 0x2E));
            IBrush addFgB = new SolidColorBrush(addFg), remFgB = new SolidColorBrush(remFg);
            IBrush addBandB = new SolidColorBrush(addBand), remBandB = new SolidColorBrush(remBand);

            var stack = new StackPanel();
            var (added, removed) = EditDiff.Counts(lines);
            stack.Children.Add(new TextBlock
            {
                Margin = new Thickness(0, 0, 0, 7), FontFamily = _p.Mono, FontSize = 11.5, FontWeight = FontWeight.SemiBold,
                Inlines = new InlineCollection
                {
                    new Run($"+{added}") { Foreground = addFgB },
                    new Run("   "),
                    new Run($"-{removed}") { Foreground = remFgB },
                },
            });

            const int cap = 200;   // a very long diff (e.g. a big new file) is clipped with a trailing note
            int shown = 0;
            foreach (var l in lines)
            {
                if (shown >= cap)
                {
                    stack.Children.Add(new TextBlock
                    {
                        Text = $"… {lines.Count - shown} more line{(lines.Count - shown == 1 ? "" : "s")}",
                        FontFamily = _p.Mono, FontSize = 11.5, Foreground = _p.Faint, Margin = new Thickness(0, 5, 0, 0),
                    });
                    break;
                }
                shown++;

                (IBrush fg, IBrush? band, string marker) = l.Kind switch
                {
                    GitDiffLineKind.Added   => ((IBrush)addFgB, (IBrush?)addBandB, "+"),
                    GitDiffLineKind.Removed => ((IBrush)remFgB, (IBrush?)remBandB, "-"),
                    GitDiffLineKind.Meta    => ((IBrush)_p.Faint, (IBrush?)null, ""),
                    _                       => ((IBrush)_p.Muted, (IBrush?)null, " "),
                };
                var rowGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("14,*") };
                rowGrid.Children.Add(new TextBlock
                {
                    Text = marker, FontFamily = _p.Mono, FontSize = 12, LineHeight = 12 * 1.55, Foreground = fg,
                    VerticalAlignment = VerticalAlignment.Top,
                });
                var code = new SelectableTextBlock
                {
                    Text = l.Text.Length == 0 ? " " : l.Text, FontFamily = _p.Mono, FontSize = 12,
                    LineHeight = 12 * 1.55, Foreground = fg, TextWrapping = TextWrapping.Wrap,
                };
                Grid.SetColumn(code, 1);
                rowGrid.Children.Add(code);
                stack.Children.Add(new Border { Background = band ?? Brushes.Transparent, Child = rowGrid });
            }
            return stack;
        }

        private static string Glyph(string tool) => tool switch
        {
            "Bash" or "PowerShell" => "❯",
            "Read"                 => "▤",
            "Edit" or "MultiEdit" or "Write" or "NotebookEdit" => "✎",
            "Grep" or "Glob"       => "⌕",
            "WebFetch" or "WebSearch" => "◎",
            "Task" or "Agent"      => "⇄",
            "TodoWrite" or "TaskCreate" or "TaskUpdate" => "☑",
            "AskUserQuestion"      => "?",
            _                      => "•",
        };

        // The absolute file path a file tool operates on, or null for a non-file tool / missing path. Claude
        // Code passes absolute paths, but a relative one is resolved against the session cwd defensively.
        private static string? FilePathOf(ToolCallPart part, string cwd)
        {
            string? raw = part.ToolName switch
            {
                "Read" or "Edit" or "MultiEdit" or "Write" => InputString(part.InputJson, "file_path"),
                "NotebookEdit"                             => InputString(part.InputJson, "notebook_path"),
                _                                          => null,
            };
            if (string.IsNullOrWhiteSpace(raw)) return null;
            try
            {
                return System.IO.Path.IsPathRooted(raw) || string.IsNullOrEmpty(cwd)
                    ? raw
                    : System.IO.Path.Combine(cwd, raw);
            }
            catch { return raw; }
        }

        // "Editing Foo.cs" → ("Editing", "Foo.cs"); "Running: npm test" → ("Running", "npm test").
        private static (string Verb, string Object) SplitSummary(string summary)
        {
            int cut = summary.IndexOf(' ');
            if (cut < 0) return (summary.TrimEnd(':'), "");
            return (summary[..cut].TrimEnd(':'), summary[(cut + 1)..]);
        }
    }

    // ── Permission ───────────────────────────────────────────────────────────────

    private Control BuildPermission(PermissionItem item)
    {
        var r = item.Request;
        if (item.Resolution != PermissionResolution.Pending) return BuildPermissionReceipt(item);
        if (item.IsQuestion) return BuildQuestion(item);
        if (item.IsPlan) return BuildPlanCard(item);

        var badge = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 7,
            Children =
            {
                new Ellipse { Width = 8, Height = 8, Fill = _p.Await, VerticalAlignment = VerticalAlignment.Center },
                new TextBlock
                {
                    Text = "PERMISSION NEEDED", FontFamily = _p.Mono, FontSize = 11.5, FontWeight = FontWeight.Bold,
                    Foreground = _p.Await, LetterSpacing = 0.6, VerticalAlignment = VerticalAlignment.Center,
                },
            },
        };
        var title = new TextBlock
        {
            Text = QuestionFor(r), FontFamily = _p.Display, FontWeight = FontWeight.Bold, FontSize = 15.5,
            Foreground = _p.Title, Margin = new Thickness(15, 6, 15, 2), TextWrapping = TextWrapping.Wrap,
        };
        var cmd = new Border
        {
            Background = _p.CodeBg, BorderBrush = _p.CodeBorder, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9), Padding = new Thickness(12, 10), Margin = new Thickness(15, 8, 15, 0),
            Child = new SelectableTextBlock
            {
                Text = CommandText(r), FontFamily = _p.Mono, FontSize = 13, Foreground = _p.Text,
                TextWrapping = TextWrapping.Wrap, LineHeight = 13 * 1.6,
            },
        };

        var allow = new SessionButton(_p, "Allow", SessionButtonKind.Primary, "↵");
        allow.Click += () => PermissionAnswered?.Invoke(item, true, false);
        var deny = new SessionButton(_p, "Deny", SessionButtonKind.Quiet, "esc");
        deny.Click += () => PermissionAnswered?.Invoke(item, false, false);
        var actions = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(15, 14, 15, 15) };
        actions.Children.Add(allow);
        if (r.SuggestedMode is { Length: > 0 } mode)
        {
            var allowMode = new SessionButton(_p, $"Allow & switch to {ModeLabel(mode)}", SessionButtonKind.Ghost)
            {
                Margin = new Thickness(9, 0, 0, 0),
            };
            allowMode.Click += () => PermissionAnswered?.Invoke(item, true, true);
            actions.Children.Add(allowMode);
        }
        deny.Margin = new Thickness(9, 0, 0, 0);
        actions.Children.Add(deny);

        var card = new Border
        {
            BorderBrush = _p.AwaitLine, BorderThickness = new Thickness(1), Background = _p.AwaitWash,
            CornerRadius = new CornerRadius(14), ClipToBounds = true,
            Child = new StackPanel
            {
                Children =
                {
                    new Border { Padding = new Thickness(15, 12, 15, 4), Child = badge },
                    title, cmd, actions,
                },
            },
        };
        var meta = new TextBlock
        {
            Text = $"awaiting your decision  ·  {r.ToolName}  ·  paused turn", FontFamily = _p.Mono, FontSize = 11.5,
            Foreground = _p.Faint, Margin = new Thickness(2, 0, 0, 0),
        };
        return new StackPanel { Spacing = 6, Children = { card, meta } };
    }

    // Claude presenting a plan to carry out (ExitPlanMode): the plan rendered as markdown, with Approve /
    // Approve & <suggested mode> / Keep planning. Approve is the normal permission allow (which lets the CLI
    // leave plan mode and proceed); Keep planning denies, so Claude stays in plan mode. Plan-blue, so it reads
    // as a decision beat distinct from a tool-permission gate.
    private Control BuildPlanCard(PermissionItem item)
    {
        var r = item.Request;
        var badge = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 7,
            Children =
            {
                new Ellipse { Width = 8, Height = 8, Fill = _p.Plan, VerticalAlignment = VerticalAlignment.Center },
                new TextBlock
                {
                    Text = "PLAN — REVIEW", FontFamily = _p.Mono, FontSize = 11.5, FontWeight = FontWeight.Bold,
                    Foreground = _p.Plan, LetterSpacing = 0.6, VerticalAlignment = VerticalAlignment.Center,
                },
            },
        };
        var title = new TextBlock
        {
            Text = "Proceed with this plan?", FontFamily = _p.Display, FontWeight = FontWeight.Bold, FontSize = 15.5,
            Foreground = _p.Title, Margin = new Thickness(15, 6, 15, 2), TextWrapping = TextWrapping.Wrap,
        };
        // The plan itself, as markdown; a long plan scrolls inside the card rather than pushing the buttons away.
        var planText = PlanApprovalInput.Parse(r.InputJson) is { Length: > 0 } md ? md : "_(no plan text)_";
        var body = new ScrollViewer
        {
            MaxHeight = 360, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(15, 8, 15, 0),
            Content = Prose(planText),
        };

        var approve = new SessionButton(_p, "Approve", SessionButtonKind.Primary, "↵");
        approve.Click += () => PermissionAnswered?.Invoke(item, true, false);
        var keep = new SessionButton(_p, "Keep planning", SessionButtonKind.Quiet, "esc");
        keep.Click += () => PermissionAnswered?.Invoke(item, false, false);
        var actions = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(15, 14, 15, 15) };
        actions.Children.Add(approve);
        if (r.SuggestedMode is { Length: > 0 } mode)
        {
            var approveMode = new SessionButton(_p, $"Approve & {ModeLabel(mode)}", SessionButtonKind.Ghost)
            {
                Margin = new Thickness(9, 0, 0, 0),
            };
            approveMode.Click += () => PermissionAnswered?.Invoke(item, true, true);
            actions.Children.Add(approveMode);
        }
        keep.Margin = new Thickness(9, 0, 0, 0);
        actions.Children.Add(keep);

        return new Border
        {
            BorderBrush = _p.PlanLine, BorderThickness = new Thickness(1), Background = _p.PlanWash,
            CornerRadius = new CornerRadius(14), ClipToBounds = true,
            Child = new StackPanel
            {
                Children =
                {
                    new Border { Padding = new Thickness(15, 12, 15, 4), Child = badge },
                    title, body, actions,
                },
            },
        };
    }

    // Claude asking the user something (AskUserQuestion): one block per question — header chip, the question,
    // its options as buttons (with descriptions). Single-select picks submit as soon as every question has an
    // answer; multi-select toggles and submits via the button. Brand-washed, so it reads as a conversation
    // beat, not a permission gate.
    private Control BuildQuestion(PermissionItem item)
    {
        var questions = AskUserQuestionInput.Parse(item.Request.InputJson);
        var chosen = new Dictionary<string, List<string>>();
        var stack = new StackPanel();
        stack.Children.Add(new Border
        {
            Padding = new Thickness(15, 12, 15, 4),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 7,
                Children =
                {
                    new Ellipse { Width = 8, Height = 8, Fill = _p.Brand, VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock
                    {
                        Text = "CLAUDE IS ASKING", FontFamily = _p.Mono, FontSize = 11.5, FontWeight = FontWeight.Bold,
                        Foreground = _p.Brand, LetterSpacing = 0.6, VerticalAlignment = VerticalAlignment.Center,
                    },
                },
            },
        });

        var submit = new SessionButton(_p, "Submit", SessionButtonKind.Primary, "↵") { Enabled = false };
        bool anyMulti = questions.Any(q => q.MultiSelect) || questions.Count > 1;
        void Submit()
        {
            var answers = chosen.Where(kv => kv.Value.Count > 0)
                .ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value.ToList());
            QuestionAnswered?.Invoke(item, answers);
        }
        void Refresh() => submit.Enabled = questions.All(q => chosen.TryGetValue(q.Question, out var l) && l.Count > 0);

        foreach (var q in questions)
        {
            var block = new StackPanel { Margin = new Thickness(15, 8, 15, 2), Spacing = 8 };
            var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            if (q.Header.Length > 0)
                head.Children.Add(new Border
                {
                    Background = _p.Raised2, BorderBrush = _p.BorderSoft, BorderThickness = new Thickness(1),
                    CornerRadius = SessionPalette.PillRadius, Padding = new Thickness(8, 2), VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock { Text = q.Header, FontFamily = _p.Mono, FontSize = 11, Foreground = _p.Muted },
                });
            head.Children.Add(new TextBlock
            {
                Text = q.Question, FontFamily = _p.Display, FontWeight = FontWeight.Bold, FontSize = 15.5,
                Foreground = _p.Title, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
            });
            block.Children.Add(head);
            if (q.MultiSelect)
                block.Children.Add(new TextBlock { Text = "choose any that apply", FontFamily = _p.Mono, FontSize = 11, Foreground = _p.Faint });

            var options = new WrapPanel { Orientation = Orientation.Horizontal };
            foreach (var o in q.Options)
            {
                var row = new StackPanel { Spacing = 2 };
                row.Children.Add(new TextBlock { Text = o.Label, FontFamily = _p.Body, FontWeight = FontWeight.SemiBold, FontSize = 14, Foreground = _p.Title });
                if (o.Description.Length > 0)
                    row.Children.Add(new TextBlock
                    {
                        Text = o.Description, FontFamily = _p.Body, FontSize = 12.5, Foreground = _p.Muted,
                        TextWrapping = TextWrapping.Wrap, MaxWidth = 260,
                    });
                var btn = new Border
                {
                    Background = _p.Raised, BorderBrush = _p.Border, BorderThickness = new Thickness(1),
                    CornerRadius = SessionPalette.ButtonRadius, Padding = new Thickness(13, 9), Margin = new Thickness(0, 0, 9, 9),
                    Cursor = new Cursor(StandardCursorType.Hand), Child = row,
                };
                var label = o.Label;
                btn.PointerEntered += (_, _) => { if (!IsPicked(q, label)) btn.Background = _p.Raised2; };
                btn.PointerExited += (_, _) => { if (!IsPicked(q, label)) btn.Background = _p.Raised; };
                btn.PointerReleased += (_, e) =>
                {
                    if (e.InitialPressMouseButton != MouseButton.Left) return;
                    var list = chosen.TryGetValue(q.Question, out var l) ? l : chosen[q.Question] = new List<string>();
                    if (q.MultiSelect)
                    {
                        if (!list.Remove(label)) list.Add(label);
                    }
                    else
                    {
                        list.Clear();
                        list.Add(label);
                    }
                    foreach (var child in options.Children.OfType<Border>())
                        Paint(child, q);
                    Refresh();
                    if (!anyMulti) Submit();   // a single single-select question: the pick is the answer
                };
                options.Children.Add(btn);
            }
            block.Children.Add(options);
            stack.Children.Add(block);
        }

        bool IsPicked(UserQuestion q, string label) => chosen.TryGetValue(q.Question, out var l) && l.Contains(label);
        void Paint(Border b, UserQuestion q)
        {
            var lbl = ((b.Child as StackPanel)?.Children[0] as TextBlock)?.Text ?? "";
            bool on = IsPicked(q, lbl);
            b.Background = on ? _p.BrandWash : _p.Raised;
            b.BorderBrush = on ? _p.BrandLine : _p.Border;
        }

        var actions = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(15, 8, 15, 15) };
        if (anyMulti) actions.Children.Add(submit);
        submit.Click += Submit;
        var skip = new SessionButton(_p, "Skip", SessionButtonKind.Quiet, "esc") { Margin = new Thickness(anyMulti ? 9 : 0, 0, 0, 0) };
        skip.Click += () => PermissionAnswered?.Invoke(item, false, false);
        actions.Children.Add(skip);
        stack.Children.Add(actions);

        var card = new Border
        {
            BorderBrush = _p.BrandLine, BorderThickness = new Thickness(1), Background = _p.BrandWash,
            CornerRadius = new CornerRadius(14), ClipToBounds = true, Child = stack,
        };
        var meta = new TextBlock
        {
            Text = "waiting for your answer  ·  paused turn", FontFamily = _p.Mono, FontSize = 11.5,
            Foreground = _p.Faint, Margin = new Thickness(2, 0, 0, 0),
        };
        return new StackPanel { Spacing = 6, Children = { card, meta } };
    }

    private Control BuildPermissionReceipt(PermissionItem item)
    {
        var r = item.Request;
        var (glyph, brush, word) = item.Resolution switch
        {
            PermissionResolution.Allowed => ("✓", _p.Ok, item.IsQuestion ? "Answered" : item.IsPlan ? "Approved plan" : "Allowed"),
            PermissionResolution.Denied  => ("✕", _p.Err, item.IsQuestion ? "Skipped" : item.IsPlan ? "Kept planning" : "Denied"),
            _                            => ("◌", _p.Faint, "Expired"),
        };
        bool prose = item.IsQuestion || item.IsPlan;   // question/plan detail reads as prose, not a mono command
        var detail = item.IsQuestion
            ? (item.AnswerSummary is { Length: > 0 } s ? s : ToolSummary.Describe(r.ToolName, ParseOrNull(r.InputJson)))
            : item.IsPlan
                ? PlanGist(r.InputJson)
                : ToolSummary.Clip(CommandText(r));
        var text = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        text.Inlines = new InlineCollection
        {
            new Run(glyph + "  ") { Foreground = brush, FontWeight = FontWeight.Bold, FontSize = 13 },
            new Run(prose ? word : word + " " + r.ToolName) { Foreground = _p.Muted, FontWeight = FontWeight.SemiBold, FontSize = 13, FontFamily = _p.Body },
            new Run("  ·  " + detail) { Foreground = _p.Faint, FontSize = 12.5, FontFamily = prose ? _p.Body : _p.Mono },
        };
        if (item.SwitchedMode is { Length: > 0 } switched)
            text.Inlines.Add(new Run($"  ·  now {ModeLabel(switched)}") { Foreground = _p.Violet, FontSize = 12.5, FontFamily = _p.Body });
        return new Border
        {
            Background = _p.Raised, BorderBrush = _p.BorderSoft, BorderThickness = new Thickness(1),
            CornerRadius = SessionPalette.CardRadius, Padding = new Thickness(13, 9), Child = text,
        };
    }

    /// <summary>The question a permission card asks, phrased per tool ("Run a terminal command?").</summary>
    internal static string QuestionFor(PermissionRequestEvent r)
    {
        string? file = InputString(r.InputJson, "file_path") ?? InputString(r.InputJson, "notebook_path");
        return r.ToolName switch
        {
            "Bash" or "PowerShell"             => "Run a terminal command?",
            "Edit" or "MultiEdit" or "NotebookEdit" => $"Edit {ToolSummary.FileLabel(file)}?",
            "Write"                            => $"Write {ToolSummary.FileLabel(file)}?",
            "Read"                             => $"Read {ToolSummary.FileLabel(file)}?",
            "WebFetch"                         => "Fetch a web page?",
            "WebSearch"                        => "Search the web?",
            "Task" or "Agent"                  => "Delegate to a sub-agent?",
            _                                  => $"Use {r.ToolName}?",
        };
    }

    /// <summary>What the card shows in its command box: the shell command for Bash, the path for file
    /// tools, else the CLI's description or the (clipped) input.</summary>
    internal static string CommandText(PermissionRequestEvent r)
    {
        switch (r.ToolName)
        {
            case "Bash" or "PowerShell":
                return "$ " + (InputString(r.InputJson, "command") ?? r.Description);
            case "Edit" or "MultiEdit" or "Write" or "NotebookEdit" or "Read":
                var path = InputString(r.InputJson, "file_path") ?? InputString(r.InputJson, "notebook_path");
                if (path is { Length: > 0 }) return path;
                break;
            case "WebFetch":
                if (InputString(r.InputJson, "url") is { Length: > 0 } url) return url;
                break;
        }
        if (r.Description.Length > 0) return r.Description;
        var pretty = PrettyJson(r.InputJson);
        return pretty.Length > 800 ? pretty[..800] + "…" : pretty;
    }

    // A one-line gist of a plan for its resolved receipt: the first meaningful line, stripped of markdown marks.
    internal static string PlanGist(string inputJson)
    {
        var plan = PlanApprovalInput.Parse(inputJson) ?? "";
        var first = plan.Split('\n')
                        .Select(l => l.Trim().TrimStart('#', '-', '*', ' '))
                        .FirstOrDefault(l => l.Length > 0) ?? "";
        return ToolSummary.Clip(first);
    }

    internal static string ModeLabel(string mode) => mode switch
    {
        "acceptEdits"       => "accept edits",
        "bypassPermissions" => "bypass permissions",
        "plan"              => "plan mode",
        "default"           => "ask",
        _                   => mode,
    };

    // ── Note ─────────────────────────────────────────────────────────────────────

    private Control BuildNote(NoteItem n) => new TextBlock
    {
        Text = n.Text, FontFamily = _p.Mono, FontSize = 11.5,
        Foreground = n.Kind == NoteKind.Error ? _p.Err : _p.Faint,
        HorizontalAlignment = HorizontalAlignment.Center, TextWrapping = TextWrapping.Wrap,
        TextAlignment = TextAlignment.Center, Margin = new Thickness(0, -8, 0, -8),
    };

    // ── Compaction progress ───────────────────────────────────────────────────────

    private Control BuildCompaction(CompactionItem item)
    {
        var card = new CompactionCard(_p, item);
        _compactions[item] = card;
        return card.Root;
    }

    /// <summary>A <c>/compact</c> in progress as a centred card with a live meter: an elapsed-seconds timer
    /// Perch drives itself (so it reads as motion even when the CLI sends no percentage) and, when a figure
    /// does arrive, a determinate bar. Settles to a "compacted · freed N" line when the turn's result lands.</summary>
    private sealed class CompactionCard
    {
        private readonly SessionPalette _p;
        private readonly ProgressBar _bar;
        private readonly TextBlock _label;
        private readonly DispatcherTimer _timer;
        private CompactionItem _item;

        public Border Root { get; }

        public CompactionCard(SessionPalette p, CompactionItem item)
        {
            _p = p;
            _item = item;
            _label = new TextBlock { FontFamily = p.Mono, FontSize = 12, Foreground = p.Muted, TextWrapping = TextWrapping.Wrap };
            _bar = new ProgressBar
            {
                Minimum = 0, Maximum = 100, Height = 5, CornerRadius = new CornerRadius(3),
                Foreground = p.Brand, Background = p.Raised2, Margin = new Thickness(0, 9, 0, 0),
            };
            Root = new Border
            {
                Background = p.Raised, BorderBrush = p.BrandLine, BorderThickness = new Thickness(1),
                CornerRadius = SessionPalette.CardRadius, Padding = new Thickness(15, 12),
                MaxWidth = SessionPalette.ThreadMaxWidth * 0.86, HorizontalAlignment = HorizontalAlignment.Center,
                Child = new StackPanel { Children = { _label, _bar } },
            };
            // Perch owns the elapsed timer, so the row animates second-by-second regardless of the stream.
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += (_, _) => Render();
            Root.AttachedToVisualTree += (_, _) => { if (!_item.IsDone) _timer.Start(); };
            Root.DetachedFromVisualTree += (_, _) => _timer.Stop();
            Render();
        }

        public void Update(CompactionItem item)
        {
            _item = item;
            Render();
            if (item.IsDone) _timer.Stop();
        }

        private void Render()
        {
            int secs = Math.Max(0, (int)(DateTime.UtcNow - _item.StartedUtc).TotalSeconds);
            if (_item.IsDone && _item.Failed)
            {
                // Interrupted / errored / nothing reclaimed — a canceled state, no green bar.
                _bar.IsVisible = false;
                _label.Foreground = _p.Err;
                _label.Text = $"✕ Compaction canceled  ·  {secs}s";
            }
            else if (_item.IsDone)
            {
                _bar.IsVisible = true;
                _bar.IsIndeterminate = false;
                _bar.Value = 100;
                _bar.Foreground = _p.Ok;
                var freed = _item.FreedTokens > 0 ? $"  ·  freed {Windows.SessionWindow.FormatTokens(_item.FreedTokens)}" : "";
                _label.Foreground = _p.Muted;
                _label.Text = $"✓ Compacted the conversation{freed}  ·  {secs}s";
            }
            else if (_item.Percent is int pct)
            {
                _bar.IsIndeterminate = false;
                _bar.Value = Math.Clamp(pct, 0, 100);
                _label.Text = $"Compacting the conversation…  ({secs}s)  ·  {pct}%";
            }
            else
            {
                _bar.IsIndeterminate = true;
                _label.Text = _item.Instructions is { Length: > 0 } kept
                    ? $"Compacting the conversation (keeping: {kept})…  ({secs}s)"
                    : $"Compacting the conversation…  ({secs}s)";
            }
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static Bitmap? _mark;

    /// <summary>The bird mark (the app icon) at the given size; an empty panel if the asset can't load.</summary>
    internal static Control MarkImage(double size)
    {
        try { _mark ??= new Bitmap(AssetLoader.Open(new Uri("avares://perch/Assets/icon.png"))); }
        catch { return new Panel { Width = size, Height = size }; }
        return new Image { Source = _mark, Width = size, Height = size, Stretch = Stretch.Uniform };
    }

    private static JsonNode? ParseOrNull(string json)
    {
        try { return JsonNode.Parse(json); } catch { return null; }
    }

    private static string? InputString(string inputJson, string key)
    {
        try { return TranscriptJson.AsString(JsonNode.Parse(inputJson)?[key]); }
        catch { return null; }
    }

    private static string PrettyJson(string json)
    {
        try
        {
            return JsonNode.Parse(json)?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? json;
        }
        catch { return json; }
    }

    // First sentence-ish of a thought, clipped, for the collapsed disclosure's label.
    private static string Gist(string thought)
    {
        var line = thought.AsSpan().TrimStart();
        int end = line.IndexOfAny('\n', '.');
        var head = end > 0 ? line[..end] : line;
        var s = head.ToString().Trim();
        return s.Length > 72 ? s[..72].TrimEnd() + "…" : s;
    }

    // "jon.howell" → "JH", "jon" → "J", "" → "You".
    private static string Initials(string userName)
    {
        var parts = userName.Split(['.', ' ', '_', '-'], StringSplitOptions.RemoveEmptyEntries);
        var s = string.Concat(parts.Take(2).Select(p => char.ToUpperInvariant(p[0])));
        return s.Length > 0 ? s : "You";
    }
}

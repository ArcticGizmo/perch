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
    private readonly Dictionary<ConversationItem, ItemView> _views = new();
    private readonly string _initials;
    private SessionConversation? _conv;
    private bool _stickToBottom = true;

    // "Claude is working" indicator: an avatar-aligned bubble of bouncing dots + the current action, kept as
    // the last child of _stack while a turn runs (and not paused on a permission). Reused across shows.
    private readonly Control _activityRow;
    private readonly TextBlock _activityLabel;
    private bool _activityShown;

    /// <summary>The user answered a permission card: (item, allow, also switch to the suggested mode).</summary>
    public event Action<PermissionItem, bool, bool>? PermissionAnswered;

    /// <summary>The user answered an AskUserQuestion card: question text → chosen labels.</summary>
    public event Action<PermissionItem, IReadOnlyDictionary<string, IReadOnlyList<string>>>? QuestionAnswered;

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
        Content = _stack;

        // Follow the tail only while the user is at (or near) the bottom; scrolling up pins the view. Only a
        // change the *user* made (offset moved, extent unchanged) re-evaluates — content growing pushes the
        // bottom away before we've scrolled to it, and must not be read as "the user scrolled up".
        ScrollChanged += (_, e) =>
        {
            if (e.ExtentDelta.Y != 0 || e.OffsetDelta.Y == 0) return;
            _stickToBottom = Offset.Y + Viewport.Height >= Extent.Height - 24;
        };

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
        _stack.Children.Clear();
        _views.Clear();
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

    // ── Items ────────────────────────────────────────────────────────────────────

    private void AddItem(ConversationItem item)
    {
        ItemView view = item switch
        {
            UserMessageItem u      => new ItemView { Root = BuildUser(u) },
            AssistantMessageItem a => BuildAssistant(a),
            PermissionItem p       => new ItemView { Root = Row(BuildPermission(p), null, null) },
            NoteItem n             => new ItemView { Root = Row(BuildNote(n), null, null) },
            _                      => new ItemView { Root = new Panel() },
        };
        _views[item] = view;
        _stack.Children.Add(view.Root);
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
                // A resolved card becomes a compact receipt: rebuild and swap in place.
                var fresh = Row(BuildPermission(p), null, null);
                int at = _stack.Children.IndexOf(view.Root);
                if (at >= 0) _stack.Children[at] = fresh;
                view.Root = fresh;
                break;
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
            : new Border
            {
                Background = _p.BrandWash, BorderBrush = _p.BrandLine, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(16, 16, 5, 16), Padding = new Thickness(15, 11),
                MaxWidth = SessionPalette.ThreadMaxWidth * 0.76,
                Child = new SelectableTextBlock
                {
                    Text = u.Text, FontSize = SessionPalette.ProseSize, FontFamily = _p.Body, Foreground = _p.Title,
                    TextWrapping = TextWrapping.Wrap, LineHeight = SessionPalette.ProseSize * 1.5,
                },
            };
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
        return Row(bubble, left: null, right: who);
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
        var view = new ItemView { Root = Row(bubble, left: avatar, right: null), Body = body };
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
                    var card = new ToolCard(_p, tool);
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

    private Control Prose(string md) => MarkdownView.Build(md, _p.Prose);

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
        private readonly SelectableTextBlock _outText;
        private ToolCallPart _part;
        private bool _expanded;

        public Border Root { get; }

        public ToolCard(SessionPalette p, ToolCallPart part)
        {
            _p = p;
            _part = part;

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
            var summary = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            summary.Inlines = new InlineCollection
            {
                new Run(verb + " ") { Foreground = p.Title, FontWeight = FontWeight.SemiBold, FontSize = 14, FontFamily = p.Body },
                new Run(obj) { Foreground = p.Muted, FontSize = 13, FontFamily = p.Mono },
            };
            _dot = new Ellipse { Width = 7, Height = 7, VerticalAlignment = VerticalAlignment.Center };
            _status = new TextBlock { FontSize = 12, FontWeight = FontWeight.SemiBold, FontFamily = p.Mono, VerticalAlignment = VerticalAlignment.Center };
            var status = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center,
                Children = { _dot, _status },
            };

            var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,11,*,11,Auto") };
            header.Children.Add(icon);
            Grid.SetColumn(summary, 2);
            header.Children.Add(summary);
            Grid.SetColumn(status, 4);
            header.Children.Add(status);
            var headerBorder = new Border
            {
                Padding = new Thickness(13, 10), Child = header, Background = Brushes.Transparent,
                Cursor = new Cursor(StandardCursorType.Hand),
            };
            headerBorder.PointerReleased += (_, e) =>
            {
                if (e.InitialPressMouseButton != MouseButton.Left) return;
                _expanded = !_expanded;
                Update(_part);
            };

            _outText = new SelectableTextBlock
            {
                FontFamily = p.Mono, FontSize = 12, LineHeight = 12 * 1.65, Foreground = p.Muted,
                TextWrapping = TextWrapping.Wrap,
            };
            _out = new Border
            {
                BorderBrush = p.BorderSoft, BorderThickness = new Thickness(0, 1, 0, 0), Background = p.CodeBg,
                Padding = new Thickness(14, 11), IsVisible = false, Child = _outText,
            };

            Root = new Border
            {
                BorderBrush = p.Border, BorderThickness = new Thickness(1), CornerRadius = SessionPalette.CardRadius,
                Background = p.Surface, ClipToBounds = true, Margin = new Thickness(0, 0, 0, 12),
                Child = new StackPanel { Children = { headerBorder, _out } },
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

            var text = part.ResultPreview;
            if (_expanded)
            {
                var args = PrettyJson(part.InputJson);
                text = text.Length > 0 ? args + "\n\n" + text : args;
            }
            _outText.Text = text;
            _outText.Foreground = part.Status == ToolCallStatus.Failed ? _p.Err : _p.Muted;
            _out.IsVisible = text.Length > 0;
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
            PermissionResolution.Allowed => ("✓", _p.Ok, item.IsQuestion ? "Answered" : "Allowed"),
            PermissionResolution.Denied  => ("✕", _p.Err, item.IsQuestion ? "Skipped" : "Denied"),
            _                            => ("◌", _p.Faint, "Expired"),
        };
        var detail = item.IsQuestion
            ? (item.AnswerSummary is { Length: > 0 } s ? s : ToolSummary.Describe(r.ToolName, ParseOrNull(r.InputJson)))
            : ToolSummary.Clip(CommandText(r));
        var text = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        text.Inlines = new InlineCollection
        {
            new Run(glyph + "  ") { Foreground = brush, FontWeight = FontWeight.Bold, FontSize = 13 },
            new Run(item.IsQuestion ? word : word + " " + r.ToolName) { Foreground = _p.Muted, FontWeight = FontWeight.SemiBold, FontSize = 13, FontFamily = _p.Body },
            new Run("  ·  " + detail) { Foreground = _p.Faint, FontSize = 12.5, FontFamily = item.IsQuestion ? _p.Body : _p.Mono },
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

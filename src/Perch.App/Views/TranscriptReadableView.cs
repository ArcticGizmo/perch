using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Perch.Avalonia.Rendering;
using Perch.Avalonia.Theming;
using Perch.Data;

namespace Perch.Avalonia.Views;

/// <summary>
/// A scrolled, live-tailed <em>readable</em> render of a session transcript — the rich reading surface
/// for the dual-surface session PoC (see <c>docs/session-control-poc.md</c> §5). Point it at a
/// <c>{sessionId}.jsonl</c> with <see cref="Attach"/> and it renders each event as its own block: prose
/// through the block-level <see cref="MarkdownView"/> (headings, code panels, tables, links), a dimmed
/// italic line for thinking, a collapsible tool-call expander, and an image affordance — the same
/// mapping the history viewer uses. New events append and a landed tool result patches its block in
/// place, driven by a debounced <see cref="FileSystemWatcher"/>, so following a busy session stays cheap.
///
/// <para>Rendering is deliberately identical to <c>HistoryWindow</c>'s readable view (the user asked to
/// reuse the renderer as-is); the block-mapping is shared in spirit with it — kept here as a
/// self-contained control so it can back any surface without depending on the history window.</para>
/// </summary>
internal sealed class TranscriptReadableView : UserControl
{
    private static readonly IBrush UserBrush  = new SolidColorBrush(Palette.Green);
    private static readonly IBrush AsstBrush  = new SolidColorBrush(Palette.Accent);
    private static readonly IBrush ToolBrush  = new SolidColorBrush(Color.FromRgb(56, 189, 248));
    private static readonly IBrush MutedBrush = Palette.MutedBrush;
    private static readonly IBrush FgBrush    = Palette.FgBrush;
    private static readonly FontFamily Mono   = new("Cascadia Code, Consolas, Menlo, monospace");

    private readonly ScrollViewer _scroll;
    private readonly FileSystemWatcher _watcher = new();
    private readonly HashSet<string> _expanded = new();
    private readonly List<Control?> _eventControls = new();

    private TranscriptParser? _parser;
    private StackPanel? _panel;
    private DispatcherTimer? _tailDebounce;
    private string? _path;

    public TranscriptReadableView()
    {
        _scroll = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Content = _scroll;
        SetPlaceholder("Waiting for the session…");

        _watcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size;
        _watcher.Changed += OnChanged;
        _watcher.Created += OnChanged;
    }

    /// <summary>Loads the transcript and begins tailing it live. Safe to call again with a new path.</summary>
    public void Attach(string transcriptPath)
    {
        Detach();
        _path = transcriptPath;
        SetPlaceholder("Loading…");
        Task.Run(() =>
        {
            var parser = new TranscriptParser(transcriptPath);
            parser.Ingest();
            return parser;
        }).ContinueWith(t =>
        {
            if (!t.IsCompletedSuccessfully) return;
            Dispatcher.UIThread.Post(() =>
            {
                if (_path != transcriptPath) return;   // re-attached to something else meanwhile
                _parser = t.Result;
                RenderAll();
                StartWatching(transcriptPath);
                _scroll.ScrollToEnd();
            });
        });
    }

    public void Detach()
    {
        StopWatching();
        _parser = null;
        _panel = null;
        _path = null;
        _eventControls.Clear();
    }

    // ── Live tail (mirrors HistoryWindow) ────────────────────────────────────────
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

    private void OnChanged(object? sender, FileSystemEventArgs e) => Dispatcher.UIThread.Post(() =>
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
            var r = _parser.Ingest();
            if (!r.HasNew && r.MutatedIndices.Count == 0) return;
            ApplyIncremental(r);
            _scroll.ScrollToEnd();
        };
        return t;
    }

    // ── Rendering (same mapping as HistoryWindow's readable view) ─────────────────
    private void RenderAll()
    {
        var events = _parser?.Events ?? [];
        if (events.Count == 0) { SetPlaceholder("This session has no readable events yet."); return; }

        var panel = new StackPanel { Margin = new Thickness(16), Spacing = 8 };
        _eventControls.Clear();
        foreach (var ev in events) AppendEvent(panel, ev);
        _panel = panel;
        _scroll.Content = panel;
    }

    private void ApplyIncremental(IngestResult r)
    {
        if (_panel is null || !ReferenceEquals(_scroll.Content, _panel)) { RenderAll(); return; }
        var events = _parser!.Events;

        if (r.HasNew)
            for (int i = r.FirstNewIndex; i < events.Count; i++)
                AppendEvent(_panel, events[i]);

        foreach (int idx in r.MutatedIndices)
        {
            if (idx < 0 || idx >= _eventControls.Count || _eventControls[idx] is not { } old) continue;
            int at = _panel.Children.IndexOf(old);
            if (at < 0) continue;
            var fresh = ToolBlock(events[idx]);
            _panel.Children[at] = fresh;
            _eventControls[idx] = fresh;
        }
    }

    private void AppendEvent(StackPanel panel, HistoryEvent ev)
    {
        Control? c = ev.Kind switch
        {
            HistoryEventKind.Meta     => null,
            HistoryEventKind.ToolCall => ToolBlock(ev),
            HistoryEventKind.Image    => ImageBlock(ev),
            _                         => ProseBlock(ev),
        };
        _eventControls.Add(c);
        if (c is not null) panel.Children.Add(c);
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

        Control body;
        if (ev.Kind == HistoryEventKind.Thinking)
        {
            body = new SelectableTextBlock
            {
                TextWrapping = TextWrapping.Wrap, Foreground = MutedBrush, FontSize = 13,
                FontStyle = FontStyle.Italic, Margin = new Thickness(0, 2, 0, 0),
                Inlines = new InlineCollection { new Run(ev.Detail) },
            };
        }
        else
        {
            body = new Border
            {
                Margin = new Thickness(0, 2, 0, 0),
                Child = MarkdownView.Build(ev.Detail.Length > 0 ? ev.Detail : ev.Summary, ProseStyle()),
            };
        }

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

    private static MarkdownStyle ProseStyle() => new(
        Fg: Palette.FgBrush, Muted: Palette.MutedBrush, Title: Palette.TitleBrush, Link: Palette.AccentBrush,
        CodeFg: Palette.FgBrush, CodeBg: Palette.ButtonBgBrush, QuoteBar: Palette.SeparatorBrush,
        Rule: Palette.SeparatorBrush, TableBorder: Palette.BorderBrush, TableHeaderBg: Palette.ButtonBgBrush,
        Syntax: Palette.Active.IsDark ? CodeSyntax.Dark() : CodeSyntax.Light());

    private void SetPlaceholder(string text) =>
        _scroll.Content = new TextBlock { Margin = new Thickness(20), Foreground = MutedBrush, Text = text };

    private static string OneLine(string s) { int nl = s.IndexOf('\n'); return nl >= 0 ? s[..nl] : s; }
    private static string ClipText(string s, int max) => s.Length > max ? s[..max].TrimEnd() + "\n… (truncated)" : s;

    private static void OpenUrl(string url) => PlatformServices.UrlOpener.Open(url);

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
}

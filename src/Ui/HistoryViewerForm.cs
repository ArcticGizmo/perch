using System.Text.RegularExpressions;
using Perch.Ui;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

using Perch.Data;
namespace Perch.Ui;

/// <summary>
/// A larger, persistent window for reading a Claude Code session's transcript. Dark custom chrome
/// (title bar, ✕, project dropdown, Readable/Raw toggle, Follow) wraps a standard read-only
/// <see cref="RichTextBox"/> so the body keeps native scrolling, selection and copy.
///
/// The dropdown lists every session transcript across all projects (active sessions first, then
/// newest-first); selecting one parses it via <see cref="TranscriptParser"/>. While an active
/// session is selected the file is tailed live (<see cref="FileSystemWatcher"/>) and new events are
/// appended. Readable view shows a conversation with one-line tool summaries you can click to
/// expand; Raw view shows the full event timeline verbatim with timestamps.
///
/// Borderless but resizable (manual edge-grip resize on the padding frame) and movable (native drag
/// from the title bar). Single reused instance owned by <see cref="OverlayApplicationContext"/>.
/// </summary>
internal sealed class HistoryViewerForm : Form
{
    // ── Palette ───────────────────────────────────────────────────────────────
    private static readonly Color BodyBg   = Color.FromArgb(18, 18, 24);
    private static readonly Color ToolColor = Theme.Accent;
    private static readonly Color SubColor  = Color.FromArgb(56, 189, 248);
    private static readonly Color CodeBg    = Color.FromArgb(32, 33, 46); // inline/fenced code highlight

    // ── Layout ────────────────────────────────────────────────────────────────
    private const int Grip       = 8;   // resize-grip / padding width around the whole window
    private const int TitleH     = 40;
    private const int ToolbarH   = 46;

    // Win32 bits used for native window drag and fast RichTextBox bulk updates.
    private const int WM_NCLBUTTONDOWN = 0xA1;
    private const int HTCAPTION        = 0x2;
    private const int WM_SETREDRAW     = 0x000B;

    // ── Chrome controls ─────────────────────────────────────────────────────────
    private readonly DoubleBufferedPanel _titleBar;
    private readonly DoubleBufferedPanel _toolbar;
    private readonly ComboBox _dropdown;
    private readonly Button _readableBtn;
    private readonly Button _rawBtn;
    private readonly Button _followBtn;
    private readonly RichTextBox _body;

    private readonly Font _uiFont      = new("Segoe UI", 9.5f, FontStyle.Regular, GraphicsUnit.Point);
    private readonly Font _boldFont    = new("Segoe UI", 9.5f, FontStyle.Bold, GraphicsUnit.Point);
    private readonly Font _italicFont  = new("Segoe UI", 9f, FontStyle.Italic, GraphicsUnit.Point);
    private readonly Font _monoFont    = new("Consolas", 9f, FontStyle.Regular, GraphicsUnit.Point);
    private readonly Font _smallFont   = new("Segoe UI", 8f, FontStyle.Regular, GraphicsUnit.Point);
    private readonly Font _titleFont   = new("Segoe UI", 10f, FontStyle.Bold, GraphicsUnit.Point);
    private readonly Font _glyphFont   = new("Segoe UI", 11f, FontStyle.Regular, GraphicsUnit.Point);

    private readonly Bitmap? _icon = EmbeddedResources.LoadBitmap("Perch.icon.png");

    // Markdown rendering: a parser for assistant/user prose, plus on-demand font caches (markdown needs
    // many size/style combinations — headers, bold, italic, strikethrough, links — so they're built lazily
    // and reused rather than created per run).
    private readonly MarkdownPipeline _pipeline =
        new MarkdownPipelineBuilder().UsePipeTables().UseEmphasisExtras().Build();
    private readonly Dictionary<(float, FontStyle), Font> _uiFonts = new();
    private readonly Dictionary<float, Font> _monoFonts = new();

    private Rectangle _closeRect;
    private bool _closeHover;

    // ── Resize state (manual edge grip) ─────────────────────────────────────────
    private enum Edge { None, L, R, T, B, TL, TR, BL, BR }
    private Edge _resizeEdge;
    private bool _resizing;
    private Point _resizeStartMouse;
    private Rectangle _resizeStartBounds;

    // ── Data / view state ───────────────────────────────────────────────────────
    private HashSet<string> _activeIds = new();
    private List<HistoryEntry> _entries = new();
    private bool _suppressSelect;
    private bool _listed;

    // Async dropdown population (the transcript scan runs off the UI thread). _pendingSelect remembers
    // a SelectSession request made before the list finished loading, applied once it's ready.
    private bool _loading;
    private bool _reloadQueued;
    private string? _pendingSelect;
    private bool _pendingSelectSet;

    private TranscriptParser? _parser;
    private string? _loadedSessionId;
    private bool _raw;
    private bool _follow;

    private readonly HashSet<string> _expanded = new();
    // Character ranges of each clickable tool-summary line in the current readable render.
    private readonly List<(int start, int end, string key)> _toolRanges = new();
    // Character ranges of clickable spans (links, image paths, pasted images) and what each does when
    // clicked — a URL/path open, or (for inline base64 images) decode-to-temp-file-and-open.
    private readonly List<(int start, int end, Action action)> _linkRanges = new();

    // Absolute Windows or Unix paths ending in an image extension, linkified inside prose so the
    // "[Image: source: C:\…\foo.png]" references Claude Code emits are clickable.
    private static readonly Regex ImagePathRegex = new(
        @"(?:[A-Za-z]:\\|/)[^\s""'<>|?*]+\.(?:png|jpe?g|gif|webp|bmp|svg)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private int _renderedCount;

    private readonly FileSystemWatcher _watcher;
    private readonly System.Windows.Forms.Timer _refreshTimer;

    public HistoryViewerForm()
    {
        Text            = "Session history";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition   = FormStartPosition.Manual;
        BackColor       = Theme.FormBg;
        ForeColor       = Theme.Fg;
        Padding         = new Padding(Grip);
        MinimumSize     = new Size(560, 400);
        KeyPreview      = true;
        DoubleBuffered  = true;
        if (_icon != null)
            Icon = Icon.FromHandle(_icon.GetHicon());

        // 50% of the screen wide, ~80% tall, centered on the screen under the cursor.
        var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
        int w = wa.Width / 2;
        int h = (int)(wa.Height * 0.8);
        Size = new Size(Math.Max(MinimumSize.Width, w), Math.Max(MinimumSize.Height, h));
        Location = new Point(wa.X + (wa.Width - Width) / 2, wa.Y + (wa.Height - Height) / 2);

        // ── Body (added first so the docked strips above sit on top of the fill) ──
        _body = new RichTextBox
        {
            Dock        = DockStyle.Fill,
            ReadOnly    = true,
            BorderStyle = BorderStyle.None,
            BackColor   = BodyBg,
            ForeColor   = Theme.Fg,
            Font        = _uiFont,
            WordWrap    = true,
            DetectUrls  = false,
            ScrollBars  = RichTextBoxScrollBars.Vertical,
            HideSelection = false,
        };
        _body.MouseUp += OnBodyMouseUp;
        _body.MouseMove += OnBodyMouseMove;

        // ── Toolbar ──
        _toolbar = new DoubleBufferedPanel { Dock = DockStyle.Top, Height = ToolbarH, BackColor = Theme.FormBg };
        _dropdown = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle     = FlatStyle.Flat,
            DrawMode      = DrawMode.OwnerDrawFixed,
            ItemHeight    = 24,
            BackColor     = Theme.ButtonBg,
            ForeColor     = Theme.Fg,
            Font          = _uiFont,
        };
        _dropdown.DrawItem += DrawDropdownItem;
        _dropdown.SelectedIndexChanged += (_, _) => LoadSelected();

        _readableBtn = FlatButton("Readable", 86);
        _readableBtn.Click += (_, _) => SetRaw(false);
        _rawBtn = FlatButton("Raw", 64);
        _rawBtn.Click += (_, _) => SetRaw(true);
        _followBtn = FlatButton("Follow", 86);
        _followBtn.Click += (_, _) => ToggleFollow();

        _toolbar.Controls.Add(_dropdown);
        _toolbar.Controls.Add(_readableBtn);
        _toolbar.Controls.Add(_rawBtn);
        _toolbar.Controls.Add(_followBtn);
        _toolbar.SizeChanged += (_, _) => LayoutToolbar();

        // ── Title bar ──
        _titleBar = new DoubleBufferedPanel { Dock = DockStyle.Top, Height = TitleH, BackColor = Theme.FormBg };
        _titleBar.Paint += OnTitleBarPaint;
        _titleBar.MouseDown += OnTitleBarMouseDown;
        _titleBar.MouseMove += OnTitleBarMouseMove;
        _titleBar.MouseLeave += (_, _) => { if (_closeHover) { _closeHover = false; _titleBar.Invalidate(); } };
        _titleBar.SizeChanged += (_, _) => LayoutTitleBar();

        Controls.Add(_body);
        Controls.Add(_toolbar);
        Controls.Add(_titleBar);

        _watcher = new FileSystemWatcher
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
            IncludeSubdirectories = false,
        };
        _watcher.Changed += OnWatcherEvent;
        _watcher.Created += OnWatcherEvent;

        _refreshTimer = new System.Windows.Forms.Timer { Interval = 150 };
        _refreshTimer.Tick += (_, _) => { _refreshTimer.Stop(); DoRefresh(); };

        LayoutTitleBar();
        LayoutToolbar();
        UpdateViewButtons();
        UpdateFollowButton();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        NativeMethods.UseDarkTitleBar(Handle);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        NativeMethods.UseDarkScrollBars(_body.Handle);
    }

    // ── Public API (driven by the owning context) ───────────────────────────────
    /// <summary>Refreshes which sessions are active (and re-lists/re-sorts the dropdown). Safe to
    /// call repeatedly as sessions come and go; the current selection is preserved.</summary>
    public void SetActiveSessions(IReadOnlyList<ClaudeSession> sessions)
    {
        var ids = sessions
            .Select(s => s.SessionId)
            .Where(id => !string.IsNullOrEmpty(id))
            .ToHashSet();

        // Re-listing 300+ transcripts and rebuilding the dropdown is only worth doing when the set of
        // active sessions actually changes (sessions start/stop) — not on every status-driven scan.
        bool changed = !_listed || !_activeIds.SetEquals(ids);
        _activeIds = ids;
        if (changed)
        {
            _listed = true;
            RefreshEntries();
        }
    }

    /// <summary>Selects the given session in the dropdown (or the most recent one if not found). If the
    /// list is still loading, the request is remembered and applied once it's ready.</summary>
    public void SelectSession(string? sessionId)
    {
        _pendingSelect = sessionId;
        _pendingSelectSet = true;
        if (!_loading) ApplySelection();
    }

    private void ApplySelection()
    {
        if (!_pendingSelectSet) return;
        _pendingSelectSet = false;

        if (_entries.Count == 0) { ShowMessage("No session history found."); return; }

        int idx = _pendingSelect == null ? 0 : _entries.FindIndex(e => e.SessionId == _pendingSelect);
        if (idx < 0) idx = 0;

        if (_dropdown.SelectedIndex != idx) _dropdown.SelectedIndex = idx; // fires LoadSelected
        else LoadSelected();
    }

    // ── Dropdown entries ─────────────────────────────────────────────────────────
    private void RefreshEntries()
    {
        if (_dropdown.DroppedDown) return;          // don't yank the list out from under an open dropdown
        if (_loading) { _reloadQueued = true; return; }

        _loading = true;
        var keep = _loadedSessionId;
        var ids = new HashSet<string>(_activeIds);  // snapshot for the background thread

        // The first scan opens 300+ transcripts to read project names; do it off the UI thread so the
        // window paints immediately instead of freezing. Results are cached, so re-lists are cheap.
        if (_dropdown.Items.Count == 0)
            ShowMessage("Loading session history…");

        UiDispatch.RunThenPost(this,
            () => SessionHistory.ListAll(ids),
            list => PopulateDropdown(list, keep),
            new List<HistoryEntry>());
    }

    private void PopulateDropdown(List<HistoryEntry> list, string? keep)
    {
        _entries = list;

        _suppressSelect = true;
        _dropdown.BeginUpdate();
        _dropdown.Items.Clear();
        foreach (var entry in _entries)
            _dropdown.Items.Add(entry);
        int keepIdx = keep == null ? -1 : _entries.FindIndex(e => e.SessionId == keep);
        if (keepIdx >= 0) _dropdown.SelectedIndex = keepIdx;
        _dropdown.EndUpdate();
        _suppressSelect = false;

        _loading = false;

        if (_pendingSelectSet)
            ApplySelection();
        else if (_loadedSessionId == null && _entries.Count > 0)
        {
            // No explicit request outstanding and nothing shown yet — default to the most recent.
            _pendingSelect = null;
            _pendingSelectSet = true;
            ApplySelection();
        }
        else
        {
            _dropdown.Invalidate();
        }

        if (_reloadQueued) { _reloadQueued = false; RefreshEntries(); }
    }

    private HistoryEntry? CurrentEntry() =>
        _dropdown.SelectedIndex >= 0 ? (HistoryEntry)_dropdown.Items[_dropdown.SelectedIndex]! : null;

    private void LoadSelected()
    {
        if (_suppressSelect) return;
        var entry = CurrentEntry();
        if (entry == null) return;
        if (entry.SessionId == _loadedSessionId) return; // already showing this one

        _loadedSessionId = entry.SessionId;
        _parser = new TranscriptParser(entry.Path);
        _parser.Ingest();
        _expanded.Clear();
        _follow = entry.IsActive;
        UpdateFollowButton();

        RenderFull();
        if (entry.IsActive) ScrollToBottom();

        SetupWatcher(entry.Path);
    }

    // ── Live tailing ─────────────────────────────────────────────────────────────
    private void SetupWatcher(string path)
    {
        try
        {
            _watcher.EnableRaisingEvents = false;
            var dir = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
            _watcher.Path = dir;
            _watcher.Filter = Path.GetFileName(path);
            _watcher.EnableRaisingEvents = true;
        }
        catch { /* best-effort: a static (inactive) session simply won't tail */ }
    }

    private void OnWatcherEvent(object sender, FileSystemEventArgs e)
    {
        try
        {
            if (IsHandleCreated && !IsDisposed)
                BeginInvoke((Action)(() => { _refreshTimer.Stop(); _refreshTimer.Start(); }));
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    private void DoRefresh()
    {
        if (_parser == null) return;
        var result = _parser.Ingest();

        // A result landing on an already-rendered call changes what's shown only when that call's
        // detail is visible (raw view, or an expanded tool in readable). Otherwise we can append.
        bool needFull = result.MutatedIndices.Any(i =>
            i < _renderedCount && (_raw || _expanded.Contains(_parser.Events[i].Key)));

        if (needFull)
        {
            RenderFull();
            if (_follow) ScrollToBottom();
            return;
        }

        if (!result.HasNew) return;

        BeginUpdate();
        AppendEvents(_renderedCount);
        EndUpdate();
        if (_follow) ScrollToBottom();
    }

    // ── Rendering ────────────────────────────────────────────────────────────────
    // keepScroll: preserve the current scroll position even when Follow is on. Used when the rebuild is
    // triggered by the user (expand/collapse) rather than by new data, so the thing they clicked stays
    // in view instead of the view snapping to the bottom.
    private void RenderFull(bool keepScroll = false)
    {
        if (!IsHandleCreated) return;
        int firstVisible = SafeFirstVisibleChar();

        BeginUpdate();
        _body.Clear();
        _toolRanges.Clear();
        _linkRanges.Clear();
        _renderedCount = 0;

        if (_parser == null || _parser.Events.Count == 0)
        {
            Append("No history recorded for this session yet.\n", Theme.Muted, _uiFont);
            EndUpdate();
            return;
        }

        AppendEvents(0);
        EndUpdate();

        if (_follow && !keepScroll) ScrollToBottom();
        else RestoreScroll(firstVisible);
    }

    private void AppendEvents(int from)
    {
        if (_parser == null) return;
        for (int i = from; i < _parser.Events.Count; i++)
            RenderEvent(_parser.Events[i]);
        _renderedCount = _parser.Events.Count;
    }

    private void RenderEvent(HistoryEvent ev)
    {
        if (!_raw && ev.Kind == HistoryEventKind.Meta) return; // meta is raw-only

        string indent = ev.IsSidechain ? "    " : "";
        string time = _raw && ev.Timestamp is { } t ? $"[{t:HH:mm:ss}] " : "";

        switch (ev.Kind)
        {
            case HistoryEventKind.UserText:
                Append("\n" + indent, _uiFont);
                Append(time + "You\n", Theme.Green, _boldFont);
                RenderBody(ev.Detail, indent, ev.IsSidechain ? 24 : 0);
                break;

            case HistoryEventKind.AssistantText:
                Append("\n" + indent, _uiFont);
                Append(time + (ev.IsSidechain ? "Sub-agent\n" : "Claude\n"), Theme.Accent, _boldFont);
                RenderBody(ev.Detail, indent, ev.IsSidechain ? 24 : 0);
                break;

            case HistoryEventKind.Thinking:
                Append("\n" + indent, _uiFont);
                Append(time + "thinking\n", Theme.Muted, _italicFont);
                Append(indent + ev.Detail + "\n", Theme.Muted, _italicFont);
                break;

            case HistoryEventKind.ToolCall:
                RenderToolCall(ev, indent, time);
                break;

            case HistoryEventKind.Image:
                RenderImage(ev, indent, time);
                break;

            case HistoryEventKind.Meta:
                Append("\n" + indent, _uiFont);
                Append(time + ev.Summary + "\n", Theme.Muted, _smallFont);
                if (!string.IsNullOrEmpty(ev.Detail))
                    Append(indent + ev.Detail + "\n", Theme.Muted, _monoFont);
                break;
        }
    }

    private void RenderToolCall(HistoryEvent ev, string indent, string time)
    {
        bool expanded = _raw || _expanded.Contains(ev.Key);
        string marker = _raw ? "" : expanded ? "▾ " : "▸ ";

        Append(indent, _uiFont);
        int start = _body.TextLength;
        Append(time + marker + "⚙ " + ev.Summary + "\n", ToolColor, _uiFont);
        int end = _body.TextLength;
        if (!_raw)
            _toolRanges.Add((start, end, ev.Key));

        if (expanded)
        {
            if (!string.IsNullOrWhiteSpace(ev.Detail))
            {
                Append(indent + "  input:\n", Theme.Muted, _smallFont);
                Append(Indented(ev.Detail, indent + "  ") + "\n", Theme.Muted, _monoFont);
            }
            if (!string.IsNullOrWhiteSpace(ev.Result))
            {
                var result = _raw ? ev.Result! : Clip(ev.Result!, 4000);
                Append(indent + "  result:\n", Theme.Muted, _smallFont);
                Append(Indented(result, indent + "  ") + "\n", Theme.Muted, _monoFont);
            }
        }
    }

    private void RenderImage(HistoryEvent ev, string indent, string time)
    {
        Append(indent, _uiFont);
        string media = ev.ImageMedia ?? "image";
        int start = _body.TextLength;
        Emit(time + $"🖼 View image ({media})", FontFor(DefaultStyle with { Link = true }), Theme.Accent);
        int end = _body.TextLength;
        Append("\n", _uiFont);

        if (!string.IsNullOrEmpty(ev.ImageUrl))
        {
            var url = ev.ImageUrl!;
            _linkRanges.Add((start, end, () => OpenUrl(url)));
        }
        else if (!string.IsNullOrEmpty(ev.ImageData))
        {
            var data = ev.ImageData!;
            var m = media;
            _linkRanges.Add((start, end, () => OpenImageData(data, m)));
        }
    }

    private void Append(string text, Color color, Font font)
    {
        _body.SelectionStart = _body.TextLength;
        _body.SelectionLength = 0;
        _body.SelectionColor = color;
        _body.SelectionFont = font;
        // Reset the paragraph/background formatting markdown rendering may have left behind, so plain
        // runs (labels, tool lines, raw text) always render flat.
        _body.SelectionBackColor = BodyBg;
        _body.SelectionIndent = 0;
        _body.SelectionHangingIndent = 0;
        _body.AppendText(text);
    }

    // Whitespace-only append (advances the caret to the end with the base font).
    private void Append(string text, Font font) => Append(text, Theme.Fg, font);

    // ── Markdown rendering (Readable view only) ──────────────────────────────────
    // Renders a user/assistant message body. In Raw view it stays verbatim; in Readable it's parsed
    // as markdown and emitted as styled runs (headers, bold/italic, code, lists, links, tables).
    private void RenderBody(string text, string indentText, int indentPx)
    {
        if (_raw)
        {
            Append(indentText + text + "\n", Theme.Fg, _uiFont);
            return;
        }
        RenderMarkdown(text, indentPx);
    }

    private void RenderMarkdown(string md, int indentPx)
    {
        if (string.IsNullOrWhiteSpace(md)) { Append("\n", _uiFont); return; }

        MarkdownDocument doc;
        try { doc = Markdown.Parse(md, _pipeline); }
        catch { Append(md.TrimEnd() + "\n", Theme.Fg, _uiFont); return; }

        foreach (var block in doc)
            RenderBlock(block, indentPx);
    }

    private void RenderBlock(Block block, int indentPx)
    {
        switch (block)
        {
            case HeadingBlock h:
            {
                float size = h.Level switch { 1 => 14f, 2 => 12.5f, 3 => 11.5f, _ => 10.5f };
                SetPara(indentPx, 0);
                Emit("\n", _uiFont, Theme.Fg);
                if (h.Inline != null)
                    RenderInlines(h.Inline, new MdStyle(size, true, false, false, false, Theme.Title));
                Emit("\n", _uiFont, Theme.Fg);
                break;
            }

            case ParagraphBlock p:
                SetPara(indentPx, 0);
                if (p.Inline != null)
                    RenderInlines(p.Inline, DefaultStyle);
                Emit("\n", _uiFont, Theme.Fg);
                break;

            case ListBlock list:
                RenderList(list, indentPx);
                break;

            case QuoteBlock q:
                foreach (var child in q)
                    RenderBlock(child, indentPx + 18);
                break;

            case Table table:
                RenderTable(table, indentPx);
                break;

            case FencedCodeBlock fc:
                RenderCode(fc, indentPx);
                break;

            case CodeBlock code:
                RenderCode(code, indentPx);
                break;

            case ThematicBreakBlock:
                SetPara(indentPx, 0);
                Emit(new string('─', 40) + "\n", _uiFont, Theme.Border);
                break;

            case HtmlBlock html:
                SetPara(indentPx, 0);
                Emit(html.Lines.ToString().Trim() + "\n", GetMono(9f), Theme.Muted);
                break;

            default:
                if (block is ContainerBlock cb)
                    foreach (var child in cb)
                        RenderBlock(child, indentPx);
                break;
        }
    }

    private void RenderList(ListBlock list, int indentPx)
    {
        const int step = 20, hanging = 16;
        int contentIndent = indentPx + step;
        int number = list.OrderedStart != null && int.TryParse(list.OrderedStart, out var s) ? s : 1;

        foreach (var itemObj in list)
        {
            if (itemObj is not ListItemBlock item) continue;
            string bullet = list.IsOrdered ? $"{number}. " : "•  ";

            SetPara(contentIndent, hanging);
            Emit(bullet, _uiFont, Theme.Muted);

            bool first = true;
            foreach (var child in item)
            {
                if (child is ParagraphBlock p)
                {
                    if (!first) SetPara(contentIndent, hanging);
                    if (p.Inline != null) RenderInlines(p.Inline, DefaultStyle);
                    Emit("\n", _uiFont, Theme.Fg);
                }
                else if (child is ListBlock nested)
                {
                    RenderList(nested, contentIndent);
                }
                else
                {
                    RenderBlock(child, contentIndent + step);
                }
                first = false;
            }
            number++;
        }
    }

    private void RenderCode(CodeBlock code, int indentPx)
    {
        var lines = code.Lines.ToString().Replace("\r", "").Split('\n').ToList();
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        if (lines.Count == 0) return;

        int max = Math.Min(lines.Max(l => l.Length), 200);
        SetPara(indentPx, 0);
        Emit("\n", _uiFont, Theme.Fg);
        foreach (var l in lines)
            Emit("  " + l.PadRight(max) + "  \n", GetMono(9f), Theme.Fg, CodeBg);
        Emit("\n", _uiFont, Theme.Fg);
    }

    private void RenderTable(Table table, int indentPx)
    {
        var rows = new List<List<string>>();
        foreach (var rowObj in table)
        {
            if (rowObj is not TableRow row) continue;
            var cells = new List<string>();
            foreach (var cellObj in row)
                if (cellObj is TableCell cell)
                    cells.Add(CellText(cell));
            rows.Add(cells);
        }
        if (rows.Count == 0) return;

        int cols = rows.Max(r => r.Count);
        var widths = new int[cols];
        foreach (var r in rows)
            for (int i = 0; i < r.Count; i++)
                widths[i] = Math.Max(widths[i], r[i].Length);

        SetPara(indentPx, 0);
        Emit("\n", _uiFont, Theme.Fg);
        for (int ri = 0; ri < rows.Count; ri++)
        {
            var r = rows[ri];
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < cols; i++)
            {
                sb.Append((i < r.Count ? r[i] : "").PadRight(widths[i]));
                if (i < cols - 1) sb.Append("  |  ");
            }
            Emit(sb.ToString() + "\n", GetMono(9f), Theme.Fg);

            if (ri == 0)
            {
                var sep = new System.Text.StringBuilder();
                for (int i = 0; i < cols; i++)
                {
                    sep.Append(new string('─', widths[i]));
                    if (i < cols - 1) sep.Append("──┼──");
                }
                Emit(sep.ToString() + "\n", GetMono(9f), Theme.Border);
            }
        }
        Emit("\n", _uiFont, Theme.Fg);
    }

    private void RenderInlines(ContainerInline container, MdStyle style)
    {
        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline lit:
                    EmitText(lit.Content.ToString(), style);
                    break;

                case CodeInline code:
                    Emit(code.Content, GetMono(9f), Theme.Fg, CodeBg);
                    break;

                case EmphasisInline em:
                {
                    var s = style;
                    if (em.DelimiterChar == '~') s = s with { Strike = true };
                    else if (em.DelimiterCount >= 2) s = s with { Bold = true };
                    else s = s with { Italic = true };
                    RenderInlines(em, s);
                    break;
                }

                case LinkInline link:
                {
                    int start = _body.TextLength;
                    if (link.IsImage)
                        Emit($"🖼 {ImageLabel(link)}", FontFor(style with { Link = true }), Theme.Accent);
                    else
                        RenderInlines(link, style with { Link = true });
                    if (!string.IsNullOrEmpty(link.Url))
                    {
                        var u = link.Url!;
                        _linkRanges.Add((start, _body.TextLength, () => OpenUrl(u)));
                    }
                    break;
                }

                case AutolinkInline auto:
                {
                    int start = _body.TextLength;
                    Emit(auto.Url, FontFor(style with { Link = true }), Theme.Accent);
                    if (!string.IsNullOrEmpty(auto.Url))
                    {
                        var u = auto.Url;
                        _linkRanges.Add((start, _body.TextLength, () => OpenUrl(u)));
                    }
                    break;
                }

                case LineBreakInline br:
                    Emit(br.IsHard ? "\n" : " ", _uiFont, Theme.Fg);
                    break;

                case HtmlInline html:
                    Emit(html.Tag, GetMono(9f), Theme.Muted);
                    break;

                case ContainerInline cc:
                    RenderInlines(cc, style);
                    break;
            }
        }
    }

    // Label for an image link: its alt text if present, otherwise the URL.
    private static string ImageLabel(LinkInline link)
    {
        var alt = PlainText(link).Trim();
        return string.IsNullOrEmpty(alt) ? (link.Url ?? "image") : alt;
    }

    // Emits literal prose, turning any absolute image path it contains into a clickable link.
    private void EmitText(string text, MdStyle style)
    {
        int pos = 0;
        foreach (Match m in ImagePathRegex.Matches(text))
        {
            if (m.Index > pos)
                Emit(text[pos..m.Index], FontFor(style), ColorFor(style));

            int start = _body.TextLength;
            Emit(m.Value, FontFor(style with { Link = true }), Theme.Accent);
            var path = m.Value;
            _linkRanges.Add((start, _body.TextLength, () => OpenUrl(path)));

            pos = m.Index + m.Length;
        }
        if (pos < text.Length)
            Emit(text[pos..], FontFor(style), ColorFor(style));
    }

    private static string CellText(TableCell cell)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var b in cell)
            if (b is LeafBlock { Inline: { } inline })
                sb.Append(PlainText(inline));
        return sb.ToString().Trim();
    }

    private static string PlainText(ContainerInline? container)
    {
        if (container == null) return "";
        var sb = new System.Text.StringBuilder();
        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline lit: sb.Append(lit.Content.ToString()); break;
                case CodeInline code: sb.Append(code.Content); break;
                case LineBreakInline: sb.Append(' '); break;
                case ContainerInline cc: sb.Append(PlainText(cc)); break;
            }
        }
        return sb.ToString();
    }

    // Emits a styled run without touching paragraph formatting (the block sets indent up front; inline
    // runs only change colour/font/background).
    private void Emit(string text, Font font, Color color, Color? back = null)
    {
        _body.SelectionStart = _body.TextLength;
        _body.SelectionLength = 0;
        _body.SelectionColor = color;
        _body.SelectionFont = font;
        _body.SelectionBackColor = back ?? BodyBg;
        _body.AppendText(text);
    }

    private void SetPara(int indentPx, int hangingPx)
    {
        _body.SelectionStart = _body.TextLength;
        _body.SelectionLength = 0;
        _body.SelectionIndent = indentPx;
        _body.SelectionHangingIndent = hangingPx;
    }

    private static readonly MdStyle DefaultStyle = new(9.5f, false, false, false, false, null);

    private readonly record struct MdStyle(
        float Size, bool Bold, bool Italic, bool Strike, bool Link, Color? Color);

    private Font FontFor(MdStyle s)
    {
        FontStyle fs = FontStyle.Regular;
        if (s.Bold) fs |= FontStyle.Bold;
        if (s.Italic) fs |= FontStyle.Italic;
        if (s.Strike) fs |= FontStyle.Strikeout;
        if (s.Link) fs |= FontStyle.Underline;
        return GetFont(s.Size, fs);
    }

    private static Color ColorFor(MdStyle s) => s.Link ? Theme.Accent : (s.Color ?? Theme.Fg);

    private Font GetFont(float size, FontStyle style)
    {
        if (!_uiFonts.TryGetValue((size, style), out var f))
            _uiFonts[(size, style)] = f = new Font("Segoe UI", size, style, GraphicsUnit.Point);
        return f;
    }

    private Font GetMono(float size)
    {
        if (!_monoFonts.TryGetValue(size, out var f))
            _monoFonts[size] = f = new Font("Consolas", size, FontStyle.Regular, GraphicsUnit.Point);
        return f;
    }

    private void BeginUpdate() => NativeMethods.SendMessage(_body.Handle, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);

    private void EndUpdate()
    {
        NativeMethods.SendMessage(_body.Handle, WM_SETREDRAW, (IntPtr)1, IntPtr.Zero);
        _body.Invalidate();
    }

    private void ScrollToBottom()
    {
        _body.SelectionStart = _body.TextLength;
        _body.SelectionLength = 0;
        _body.ScrollToCaret();
    }

    private void RestoreScroll(int charIndex)
    {
        _body.SelectionStart = Math.Clamp(charIndex, 0, _body.TextLength);
        _body.SelectionLength = 0;
        _body.ScrollToCaret();
    }

    private int SafeFirstVisibleChar()
    {
        try { return _body.GetCharIndexFromPosition(new Point(2, 2)); }
        catch { return 0; }
    }

    private void ShowMessage(string msg)
    {
        _parser = null;
        _loadedSessionId = null;
        BeginUpdate();
        _body.Clear();
        _toolRanges.Clear();
        _renderedCount = 0;
        Append(msg + "\n", Theme.Muted, _uiFont);
        EndUpdate();
    }

    // ── Expand / collapse a tool call ────────────────────────────────────────────
    private void OnBodyMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        if (_body.SelectionLength > 0) return; // a text selection, not a click

        int idx = _body.GetCharIndexFromPosition(e.Location);

        // Links / image references / pasted images open — in both views.
        foreach (var (start, end, action) in _linkRanges)
        {
            if (idx >= start && idx < end) { action(); return; }
        }

        if (_raw) return; // tool expand/collapse is a Readable-only affordance

        // A tool-summary line toggles its detail, keeping the clicked line in view.
        foreach (var (start, end, key) in _toolRanges)
        {
            if (idx >= start && idx < end)
            {
                if (!_expanded.Remove(key)) _expanded.Add(key);
                RenderFull(keepScroll: true);
                return;
            }
        }
    }

    private void OnBodyMouseMove(object? sender, MouseEventArgs e)
    {
        int idx = _body.GetCharIndexFromPosition(e.Location);
        bool clickable = false;
        foreach (var (start, end, _) in _linkRanges)
            if (idx >= start && idx < end) { clickable = true; break; }
        if (!clickable && !_raw)
            foreach (var (start, end, _) in _toolRanges)
                if (idx >= start && idx < end) { clickable = true; break; }
        _body.Cursor = clickable ? Cursors.Hand : Cursors.IBeam;
    }

    private static void OpenUrl(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* no handler / blocked path — nothing useful to do */ }
    }

    // Decodes an inline base64 image to a temp file and opens it in the default viewer.
    private static void OpenImageData(string base64, string media)
    {
        try
        {
            var bytes = Convert.FromBase64String(base64);
            string ext = media switch
            {
                "image/png"  => ".png",
                "image/jpeg" => ".jpg",
                "image/gif"  => ".gif",
                "image/webp" => ".webp",
                "image/bmp"  => ".bmp",
                _            => ".png",
            };
            var path = Path.Combine(Path.GetTempPath(), $"claudewatch-img-{Guid.NewGuid():N}{ext}");
            File.WriteAllBytes(path, bytes);
            OpenUrl(path);
        }
        catch { /* malformed data — nothing to open */ }
    }

    // ── View / follow toggles ────────────────────────────────────────────────────
    private void SetRaw(bool raw)
    {
        if (_raw == raw) return;
        _raw = raw;
        UpdateViewButtons();
        RenderFull();
    }

    private void ToggleFollow()
    {
        _follow = !_follow;
        UpdateFollowButton();
        if (_follow) ScrollToBottom();
    }

    private void UpdateViewButtons()
    {
        ThemedControls.StyleToggle(_readableBtn, !_raw);
        ThemedControls.StyleToggle(_rawBtn, _raw);
    }

    private void UpdateFollowButton()
    {
        _followBtn.Text = _follow ? "Following" : "Follow";
        ThemedControls.StyleToggle(_followBtn, _follow);
    }

    // ── Toolbar / title layout ───────────────────────────────────────────────────
    private void LayoutToolbar()
    {
        const int pad = 8, gap = 6;
        int h = _toolbar.Height;
        int btnH = 26;
        int y = (h - btnH) / 2;

        int x = _toolbar.Width - pad;
        _followBtn.SetBounds(x - _followBtn.Width, y, _followBtn.Width, btnH);
        x = _followBtn.Left - gap * 2;

        // Readable | Raw rendered as an adjacent segmented pair.
        _rawBtn.SetBounds(x - _rawBtn.Width, y, _rawBtn.Width, btnH);
        _readableBtn.SetBounds(_rawBtn.Left - _readableBtn.Width, y, _readableBtn.Width, btnH);
        x = _readableBtn.Left - gap * 2;

        int ddH = _dropdown.PreferredHeight;
        int ddW = Math.Max(120, x - pad);
        _dropdown.SetBounds(pad, (h - ddH) / 2, ddW, ddH);
    }

    private void LayoutTitleBar() =>
        _closeRect = new Rectangle(_titleBar.Width - 38, (_titleBar.Height - 24) / 2, 26, 24);

    private void OnTitleBarPaint(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.FormBg);
        TextRenderer.DrawText(g, "Session history", _titleFont,
            new Rectangle(12, 0, _titleBar.Width - 60, _titleBar.Height), Theme.Title,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        TextRenderer.DrawText(g, "✕", _glyphFont, _closeRect,
            _closeHover ? Theme.Fg : Theme.Muted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    private void OnTitleBarMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        if (_closeRect.Contains(e.Location)) { Close(); return; }
        // Native window drag from the title bar.
        NativeMethods.ReleaseCapture();
        NativeMethods.SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
    }

    private void OnTitleBarMouseMove(object? sender, MouseEventArgs e)
    {
        bool hover = _closeRect.Contains(e.Location);
        if (hover != _closeHover)
        {
            _closeHover = hover;
            _titleBar.Cursor = hover ? Cursors.Hand : Cursors.Default;
            _titleBar.Invalidate(_closeRect);
        }
    }

    // ── Dropdown owner-draw ──────────────────────────────────────────────────────
    private void DrawDropdownItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _dropdown.Items.Count) return;
        var entry = (HistoryEntry)_dropdown.Items[e.Index]!;
        var g = e.Graphics;
        bool selected = e.State.HasFlag(DrawItemState.Selected);

        using (var bg = new SolidBrush(selected ? Theme.ButtonHover : Theme.ButtonBg))
            g.FillRectangle(bg, e.Bounds);

        int x = e.Bounds.Left + 8;
        int midY = e.Bounds.Top + e.Bounds.Height / 2;

        if (entry.IsActive)
        {
            using var dot = new SolidBrush(Theme.Green);
            g.FillEllipse(dot, x, midY - 4, 8, 8);
            x += 14;
        }

        string label = $"{entry.ProjectName}    ·    {entry.RelativeTime}";
        int rightReserve = entry.IsActive ? 64 : 8;
        TextRenderer.DrawText(g, label, _uiFont,
            new Rectangle(x, e.Bounds.Top, e.Bounds.Right - x - rightReserve, e.Bounds.Height),
            Theme.Fg, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

        if (entry.IsActive)
            TextRenderer.DrawText(g, "active", _uiFont,
                new Rectangle(e.Bounds.Right - 60, e.Bounds.Top, 54, e.Bounds.Height),
                Theme.Green, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
    }

    // ── Manual edge-grip resize (borderless but resizable) ───────────────────────
    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_resizing)
        {
            ApplyResize();
        }
        else
        {
            var edge = HitEdge(e.Location);
            Cursor = edge switch
            {
                Edge.L or Edge.R => Cursors.SizeWE,
                Edge.T or Edge.B => Cursors.SizeNS,
                Edge.TL or Edge.BR => Cursors.SizeNWSE,
                Edge.TR or Edge.BL => Cursors.SizeNESW,
                _ => Cursors.Default,
            };
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            var edge = HitEdge(e.Location);
            if (edge != Edge.None)
            {
                _resizing = true;
                _resizeEdge = edge;
                _resizeStartMouse = Cursor.Position;
                _resizeStartBounds = Bounds;
            }
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        _resizing = false;
        base.OnMouseUp(e);
    }

    private Edge HitEdge(Point p)
    {
        bool l = p.X <= Grip, r = p.X >= ClientSize.Width - Grip;
        bool t = p.Y <= Grip, b = p.Y >= ClientSize.Height - Grip;
        if (t && l) return Edge.TL;
        if (t && r) return Edge.TR;
        if (b && l) return Edge.BL;
        if (b && r) return Edge.BR;
        if (l) return Edge.L;
        if (r) return Edge.R;
        if (t) return Edge.T;
        if (b) return Edge.B;
        return Edge.None;
    }

    private void ApplyResize()
    {
        var c = Cursor.Position;
        int dx = c.X - _resizeStartMouse.X;
        int dy = c.Y - _resizeStartMouse.Y;
        var b = _resizeStartBounds;

        int left = b.Left, top = b.Top, width = b.Width, height = b.Height;

        if (_resizeEdge is Edge.L or Edge.TL or Edge.BL) { left = b.Left + dx; width = b.Width - dx; }
        if (_resizeEdge is Edge.R or Edge.TR or Edge.BR) { width = b.Width + dx; }
        if (_resizeEdge is Edge.T or Edge.TL or Edge.TR) { top = b.Top + dy; height = b.Height - dy; }
        if (_resizeEdge is Edge.B or Edge.BL or Edge.BR) { height = b.Height + dy; }

        if (width < MinimumSize.Width)
        {
            if (_resizeEdge is Edge.L or Edge.TL or Edge.BL) left = b.Right - MinimumSize.Width;
            width = MinimumSize.Width;
        }
        if (height < MinimumSize.Height)
        {
            if (_resizeEdge is Edge.T or Edge.TL or Edge.TR) top = b.Bottom - MinimumSize.Height;
            height = MinimumSize.Height;
        }

        Bounds = new Rectangle(left, top, width, height);
    }

    // ── Border paint (1px frame around the padding) ──────────────────────────────
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Theme.Border, 1f);
        e.Graphics.DrawRectangle(pen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape)
        {
            Close();
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    private static Button FlatButton(string text, int width)
    {
        var b = ThemedControls.FlatButton(text);
        b.Width   = width;
        b.Height  = 26;
        b.Font    = new Font("Segoe UI", 8.5f, FontStyle.Regular, GraphicsUnit.Point);
        b.TabStop = false;
        return b;
    }

    // Re-indents a multi-line block so wrapped detail lines sit under their header.
    private static string Indented(string text, string indent)
    {
        if (string.IsNullOrEmpty(indent)) return text;
        return indent + text.Replace("\n", "\n" + indent);
    }

    private static string Clip(string s, int max) =>
        s.Length <= max ? s : s[..max] + $"\n… ({s.Length - max} more characters — switch to Raw for the full result)";

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _watcher.Dispose();
            _refreshTimer.Dispose();
            _uiFont.Dispose();
            _boldFont.Dispose();
            _italicFont.Dispose();
            _monoFont.Dispose();
            _smallFont.Dispose();
            _titleFont.Dispose();
            _glyphFont.Dispose();
            foreach (var f in _uiFonts.Values) f.Dispose();
            foreach (var f in _monoFonts.Values) f.Dispose();
            _icon?.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>A Panel with double-buffering enabled, so the custom-painted chrome doesn't flicker.</summary>
    private sealed class DoubleBufferedPanel : Panel
    {
        public DoubleBufferedPanel() => DoubleBuffered = true;
    }
}

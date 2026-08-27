using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Perch.Avalonia.Theming;
using Perch.Data;
using Perch.Data.Control;
using Perch.Platform;

namespace Perch.Avalonia.Windows;

/// <summary>
/// PoC: a Perch-<em>controlled</em> Claude Code session — Perch spawns the CLI over the bidirectional
/// stream-json interface (<see cref="ClaudeSessionController"/>) and renders the conversation as rich
/// blocks instead of a terminal: streamed assistant text, dimmed thinking, tool chips with results, an
/// inline permission bar (Allow / Deny / the CLI's suggested mode switch), a live permission-mode
/// switcher, and interrupt. See <c>docs/session-control-poc.md</c> for scope and findings.
/// </summary>
internal sealed class SessionConsoleWindow : Window
{
    private readonly TextBox _cwdBox;
    private readonly ComboBox _modelCombo;
    private readonly ComboBox _modeCombo;
    private readonly Button _startButton;
    private readonly Button _interruptButton;
    private readonly TextBlock _statusLabel;
    private readonly StackPanel _transcript;
    private readonly ScrollViewer _scroll;
    private readonly Border _permBar;
    private readonly TextBlock _permLabel;
    private readonly Button _permAllowMode;
    private readonly TextBox _input;
    private readonly Button _sendButton;

    private ClaudeSessionController? _controller;
    private string? _resumeId;                     // set when elevating/hand-off resumes an existing session
    private readonly Button _handBackButton;
    private PermissionRequestEvent? _pendingPermission;
    private SelectableTextBlock? _streamBlock;                    // live delta accumulator, finalised per text block
    private readonly Dictionary<string, TextBlock> _toolChips = new();
    private bool _suppressModeSend;                               // guards combo updates that echo a CLI ack
    private bool _turnActive;                                     // a turn is running; further sends queue
    private int _queued;                                          // prompts queued behind the running turn
    private string _sessionInfo = "";                            // the init line; the status shows it + queue
    private bool _closed;

    private static readonly string[] Modes = ["default", "plan", "acceptEdits", "bypassPermissions"];

    public SessionConsoleWindow()
    {
        Title = "Session console (PoC)";
        Width = 760;
        Height = 640;
        MinWidth = 520;
        MinHeight = 400;
        Background = Palette.SurfaceSunkenBrush;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _cwdBox = new TextBox
        {
            Text = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            PlaceholderText = "Project folder", FontSize = 12, MinWidth = 260,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _modelCombo = new ComboBox
        {
            ItemsSource = new[] { "(default model)", "haiku", "sonnet", "opus" }, SelectedIndex = 0,
            FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
        };
        _modeCombo = new ComboBox
        {
            ItemsSource = Modes, SelectedIndex = 0, FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
        };
        _modeCombo.SelectionChanged += (_, _) => OnModeSelected();
        _startButton = new Button
        {
            Content = "Start session", FontSize = 12, CornerRadius = new CornerRadius(6),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _startButton.Click += (_, _) => StartSession();
        _interruptButton = new Button
        {
            Content = "Interrupt", FontSize = 12, CornerRadius = new CornerRadius(6),
            VerticalAlignment = VerticalAlignment.Center, IsEnabled = false,
        };
        _interruptButton.Click += (_, _) => _controller?.Interrupt();
        _handBackButton = new Button
        {
            Content = "Hand back to terminal", FontSize = 12, CornerRadius = new CornerRadius(6),
            VerticalAlignment = VerticalAlignment.Center, IsEnabled = false,
            IsVisible = false,   // only meaningful once a session is running
        };
        _handBackButton.Click += (_, _) => HandBackToTerminal();
        _statusLabel = new TextBlock
        {
            Text = "not started", Foreground = Palette.MutedBrush, FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(12, 9),
            Children = { _cwdBox, _modelCombo, _modeCombo, _startButton, _interruptButton, _handBackButton, _statusLabel },
        };
        var toolbarPanel = new Border { Background = Palette.FormBgBrush, Child = toolbar, [DockPanel.DockProperty] = Dock.Top };

        _transcript = new StackPanel { Spacing = 8, Margin = new Thickness(14, 12) };
        _scroll = new ScrollViewer { Content = _transcript, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };

        // Permission bar: hidden until the CLI raises a can_use_tool control request; blocks the turn
        // until answered, so it sits right above the input where a reply would go.
        _permLabel = new TextBlock
        {
            Foreground = Palette.TitleBrush, FontSize = 12, TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var allow = new Button { Content = "Allow", FontSize = 12, CornerRadius = new CornerRadius(6) };
        allow.Click += (_, _) => AnswerPermission(true, switchMode: false);
        _permAllowMode = new Button { Content = "Allow + accept edits", FontSize = 12, CornerRadius = new CornerRadius(6) };
        _permAllowMode.Click += (_, _) => AnswerPermission(true, switchMode: true);
        var deny = new Button { Content = "Deny", FontSize = 12, CornerRadius = new CornerRadius(6) };
        deny.Click += (_, _) => AnswerPermission(false, switchMode: false);
        var permButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { allow, _permAllowMode, deny } };
        permButtons.HorizontalAlignment = HorizontalAlignment.Right;
        var permGrid = new DockPanel { Children = { permButtons, _permLabel } };
        permButtons[DockPanel.DockProperty] = Dock.Right;
        _permBar = new Border
        {
            Background = Palette.FormBgBrush, BorderBrush = Palette.AwaitingBrush, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 8), Margin = new Thickness(12, 0, 12, 8),
            Child = permGrid, IsVisible = false, [DockPanel.DockProperty] = Dock.Bottom,
        };

        _input = new TextBox
        {
            PlaceholderText = "Message Claude… (Enter to send, Shift+Enter for a new line)",
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontSize = 13,
            MaxHeight = 120, IsEnabled = false,
        };
        _input.KeyDown += OnInputKeyDown;
        _sendButton = new Button
        {
            Content = "Send", FontSize = 12, CornerRadius = new CornerRadius(6), IsEnabled = false,
            VerticalAlignment = VerticalAlignment.Stretch, Margin = new Thickness(8, 0, 0, 0),
        };
        _sendButton.Click += (_, _) => SendPrompt();
        var inputRow = new DockPanel { Margin = new Thickness(12, 0, 12, 12), Children = { _sendButton, _input } };
        _sendButton[DockPanel.DockProperty] = Dock.Right;
        inputRow[DockPanel.DockProperty] = Dock.Bottom;

        Content = new DockPanel { Children = { toolbarPanel, inputRow, _permBar, _scroll } };
    }

    // ── Session lifecycle ───────────────────────────────────────────────────────

    private void StartSession()
    {
        var cwd = _cwdBox.Text?.Trim() ?? "";
        if (!Directory.Exists(cwd))
        {
            AddSystemLine($"Folder not found: {cwd}");
            return;
        }

        var model = _modelCombo.SelectedIndex > 0 ? _modelCombo.SelectedItem as string : null;
        var mode = _modeCombo.SelectedItem as string;

        var controller = new ClaudeSessionController();
        controller.EventReceived += ev => Dispatcher.UIThread.Post(() => { if (!_closed) HandleEvent(ev); });
        controller.Exited += (code, err) => Dispatcher.UIThread.Post(() => { if (!_closed) OnSessionExited(code, err); });
        try
        {
            controller.Start(cwd, model, mode, _resumeId);
        }
        catch (Exception ex)
        {
            AddSystemLine($"Failed to start claude: {ex.Message}");
            controller.Dispose();
            return;
        }

        if (_resumeId is not null) AddSystemLine($"resuming session {Shorten(_resumeId)} …");
        _controller = controller;
        _statusLabel.Text = "starting…";
        _startButton.IsEnabled = false;
        _cwdBox.IsEnabled = false;
        _modelCombo.IsEnabled = false;
        _interruptButton.IsEnabled = true;
        _handBackButton.IsVisible = true;
        _handBackButton.IsEnabled = true;
        _input.IsEnabled = true;
        _sendButton.IsEnabled = true;
        _input.Focus();
    }

    /// <summary>Opens (or focuses) the console already resuming an existing session by id — the target of
    /// the overlay's "Elevate to Perch" action (session-control M4). The caller has already stopped the
    /// terminal-side process; here we take over its transcript via <c>--resume</c>.</summary>
    public void ResumeSession(string sessionId, string cwd, string? model = null)
    {
        _resumeId = sessionId;
        if (Directory.Exists(cwd)) _cwdBox.Text = cwd;
        if (!string.IsNullOrEmpty(model))
        {
            var idx = Array.FindIndex((string[])_modelCombo.ItemsSource!, m => m == model);
            if (idx >= 0) _modelCombo.SelectedIndex = idx;
        }
        if (_controller is null) StartSession();
    }

    // Stops the Perch-owned session and reopens it in a real terminal via `claude --resume <id>`, the
    // reverse of "Elevate to Perch" — the conversation continues under the same id (session-control M4).
    private void HandBackToTerminal()
    {
        var id = _controller?.SessionId;
        if (string.IsNullOrEmpty(id))
        {
            AddSystemLine("no live session id yet — can't hand back");
            return;
        }
        var cwd = _cwdBox.Text?.Trim() ?? "";
        _handBackButton.IsEnabled = false;
        AddSystemLine($"handing session {Shorten(id)} back to a terminal…");
        _controller?.Stop();   // OnSessionExited resets the toolbar when the process ends
        try { PlatformServices.SessionLauncher.Reopen(cwd, id, TerminalApp.Auto); }
        catch (Exception ex) { AddSystemLine($"couldn't open terminal: {ex.Message}"); }
    }

    private void OnSessionExited(int exitCode, string stderrTail)
    {
        AddSystemLine(exitCode == 0 ? "session ended" : $"claude exited ({exitCode}) {stderrTail}".TrimEnd());
        _statusLabel.Text = "ended";
        _controller?.Dispose();
        _controller = null;
        _pendingPermission = null;
        _permBar.IsVisible = false;
        _streamBlock = null;
        _toolChips.Clear();
        _turnActive = false;
        _queued = 0;
        _resumeId = null;                    // a fresh "Start" is a new session, not a re-resume
        _startButton.IsEnabled = true;       // the window can host a fresh session
        _startButton.Content = "New session";
        _cwdBox.IsEnabled = true;
        _modelCombo.IsEnabled = true;
        _interruptButton.IsEnabled = false;
        _handBackButton.IsEnabled = false;
        _input.IsEnabled = false;
        _sendButton.IsEnabled = false;
    }

    // ── Event rendering ─────────────────────────────────────────────────────────

    private void HandleEvent(SessionEvent ev)
    {
        switch (ev)
        {
            case SessionInitEvent init:
                _sessionInfo = $"{Shorten(init.SessionId)} · {init.Model} · {init.ToolCount} tools";
                UpdateStatus();
                SyncModeCombo(init.PermissionMode);
                break;
            case TextDeltaEvent delta:
                if (_streamBlock is null)
                {
                    _streamBlock = MakeText("", Palette.FgBrush);
                    _transcript.Children.Add(_streamBlock);
                }
                _streamBlock.Text += delta.Text;
                _scroll.ScrollToEnd();
                break;
            case AssistantTextEvent text:
                // The completed block supersedes the plain delta accumulator: swap in a rich Markdown
                // render (headings, code panels, tables) so a long answer reads like Claude Desktop, not
                // a wall of terminal text. During streaming _streamBlock shows raw deltas for immediacy.
                var rendered = MarkdownBlock(text.Text);
                if (_streamBlock is not null)
                {
                    int at = _transcript.Children.IndexOf(_streamBlock);
                    if (at >= 0) _transcript.Children[at] = rendered; else _transcript.Children.Add(rendered);
                    _streamBlock = null;
                }
                else _transcript.Children.Add(rendered);
                _scroll.ScrollToEnd();
                break;
            case AssistantThinkingEvent thinking:
                var clipped = thinking.Text.Length > 400 ? thinking.Text[..400] + "…" : thinking.Text;
                var block = MakeText(clipped, Palette.MutedBrush);
                block.FontStyle = FontStyle.Italic;
                block.FontSize = 12;
                _transcript.Children.Add(block);
                _scroll.ScrollToEnd();
                break;
            case ToolUseEvent tool:
                var chip = MakeText($"▸ {tool.Summary}", Palette.MutedBrush);
                chip.FontSize = 12;
                if (tool.ToolUseId.Length > 0) _toolChips[tool.ToolUseId] = chip;
                _transcript.Children.Add(chip);
                _scroll.ScrollToEnd();
                break;
            case ToolResultEvent result:
                if (_toolChips.Remove(result.ToolUseId, out var owner))
                {
                    if (result.IsError)
                    {
                        owner.Text += $" — {result.Preview}";
                        owner.Foreground = Palette.ErrorBrush;
                    }
                    else owner.Text += " ✓";
                }
                break;
            case PermissionRequestEvent permission:
                _pendingPermission = permission;
                var what = permission.Description.Length > 0 ? permission.Description : permission.InputJson;
                _permLabel.Text = $"Allow {permission.ToolName}? {what}";
                _permAllowMode.IsVisible = permission.SuggestedMode == "acceptEdits";
                _permBar.IsVisible = true;
                break;
            case ModeChangedEvent mode:
                SyncModeCombo(mode.Mode);
                AddSystemLine($"permission mode → {mode.Mode}");
                break;
            case TurnResultEvent result:
                _streamBlock = null;
                if (_queued > 0) _queued--; else _turnActive = false;   // a queued turn now runs
                UpdateStatus();
                AddSystemLine(result.IsError
                    ? $"turn failed ({result.Subtype})"
                    : $"turn done · ${result.CostUsd:0.00} · {result.OutputTokens} out tokens");
                break;
        }
    }

    private void AnswerPermission(bool allow, bool switchMode)
    {
        if (_pendingPermission is not { } request || _controller is not { } controller) return;
        controller.RespondToPermission(request, allow);
        if (allow && switchMode && request.SuggestedMode is { } mode) controller.SetPermissionMode(mode);
        AddSystemLine($"{(allow ? "allowed" : "denied")} {request.ToolName}");
        _pendingPermission = null;
        _permBar.IsVisible = false;
    }

    // ── Input ───────────────────────────────────────────────────────────────────

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            SendPrompt();
            e.Handled = true;
        }
    }

    private void SendPrompt()
    {
        var text = _input.Text?.Trim();
        if (string.IsNullOrEmpty(text) || _controller is not { IsRunning: true } controller) return;
        controller.SendPrompt(text);
        _input.Text = "";

        // The CLI runs one turn at a time; a prompt sent mid-turn queues. Track it so the status shows
        // how many are waiting (stream-json accepts them all, drained in order).
        if (_turnActive) _queued++; else _turnActive = true;
        UpdateStatus();

        var bubble = new Border
        {
            Background = Palette.OverlayRowHoverBrush, CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 6), Child = MakeText(text, Palette.TitleBrush),
        };
        _transcript.Children.Add(bubble);
        _scroll.ScrollToEnd();
    }

    private void UpdateStatus() =>
        _statusLabel.Text = _queued > 0 ? $"{_sessionInfo} · {_queued} queued" : _sessionInfo;

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private void OnModeSelected()
    {
        if (_suppressModeSend || _controller is not { IsRunning: true } controller) return;
        if (_modeCombo.SelectedItem is string mode) controller.SetPermissionMode(mode);
    }

    private void SyncModeCombo(string mode)
    {
        var index = Array.IndexOf(Modes, mode);
        if (index < 0 || index == _modeCombo.SelectedIndex) return;
        _suppressModeSend = true;
        _modeCombo.SelectedIndex = index;
        _suppressModeSend = false;
    }

    private void AddSystemLine(string text)
    {
        var line = MakeText(text, Palette.MutedBrush);
        line.FontSize = 11;
        _transcript.Children.Add(line);
        _scroll.ScrollToEnd();
    }

    private static SelectableTextBlock MakeText(string text, IBrush brush) => new()
    {
        Text = text, Foreground = brush, FontSize = 13, TextWrapping = TextWrapping.Wrap,
    };

    // A completed assistant message rendered through the block-level MarkdownView (same styling as the
    // history mirror), so code, tables and headings read richly. Best-effort: parse failure → raw text.
    private static Control MarkdownBlock(string md) => Perch.Avalonia.Rendering.MarkdownView.Build(md, ProseStyle());

    private static Perch.Avalonia.Rendering.MarkdownStyle ProseStyle() => new(
        Fg: Palette.FgBrush, Muted: Palette.MutedBrush, Title: Palette.TitleBrush, Link: Palette.AccentBrush,
        CodeFg: Palette.FgBrush, CodeBg: Palette.ButtonBgBrush, QuoteBar: Palette.SeparatorBrush,
        Rule: Palette.SeparatorBrush, TableBorder: Palette.BorderBrush, TableHeaderBg: Palette.ButtonBgBrush,
        Syntax: Palette.Active.IsDark
            ? Perch.Avalonia.Rendering.CodeSyntax.Dark()
            : Perch.Avalonia.Rendering.CodeSyntax.Light());

    private static string Shorten(string sessionId) => sessionId.Length > 8 ? sessionId[..8] : sessionId;

    /// <summary>HeadlessRenderer hook: feed synthetic events into the transcript (no process), so the
    /// rich rendering — user bubble, thinking, tool chips, Markdown answer — can be captured.</summary>
    internal void FeedSampleForRender(IEnumerable<SessionEvent> events)
    {
        _sessionInfo = "a1b2c3d4 · claude-haiku-4-5 · 16 tools";
        UpdateStatus();
        foreach (var ev in events) HandleEvent(ev);
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _controller?.Dispose();   // Stop(): stdin close + tree kill; the session stays resumable on disk
        _controller = null;
        base.OnClosed(e);
    }
}

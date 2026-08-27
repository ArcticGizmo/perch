using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Iciclecreek.Terminal;
using Perch.Avalonia.Theming;

namespace Perch.Avalonia.Windows;

/// <summary>
/// A real interactive <c>claude</c> session running in an embedded ConPTY terminal (session-control —
/// the ConPTY pivot, see docs/session-control-plan.md). The genuine Claude Code TUI renders inside a
/// Perch window via <see cref="TerminalControl"/> (XTerm.NET emulator over Porta.Pty), so the user keeps
/// full terminal control — <em>and</em> a Perch input box below it writes prompts into the same PTY
/// (<see cref="TerminalControl.SendInputAsync"/>), so a prompt can come from the terminal or from Perch.
///
/// <para>This replaces the stream-json chat console as the elevation destination: launching / resuming a
/// session here means no orphaned external terminal spewing teardown escapes — the terminal <em>is</em>
/// the Perch window.</para>
/// </summary>
internal sealed class SessionTerminalWindow : Window
{
    private static readonly FontFamily Mono = new("Cascadia Mono, Cascadia Code, Consolas, Menlo, monospace");

    private readonly TextBox _cwdBox;
    private readonly Button _startButton;
    private readonly TextBlock _statusLabel;
    private readonly TerminalControl _terminal;
    private readonly TextBox _promptBox;
    private readonly Button _sendButton;

    private string? _resumeId;
    private bool _launched;

    public SessionTerminalWindow()
    {
        Title = "Session terminal (PoC)";
        Width = 900;
        Height = 640;
        MinWidth = 560;
        MinHeight = 380;
        Background = Palette.SurfaceSunkenBrush;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _cwdBox = new TextBox
        {
            Text = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            PlaceholderText = "Project folder", FontSize = 12, MinWidth = 320,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _startButton = new Button
        {
            Content = "Start claude", FontSize = 12, CornerRadius = new CornerRadius(6),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _startButton.Click += (_, _) => Launch();
        _statusLabel = new TextBlock
        {
            Text = "not started", Foreground = Palette.MutedBrush, FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(12, 9),
            Children = { _cwdBox, _startButton, _statusLabel },
        };
        var toolbarPanel = new Border { Background = Palette.FormBgBrush, Child = toolbar, [DockPanel.DockProperty] = Dock.Top };

        _terminal = new TerminalControl
        {
            FontFamily = Mono,
            FontSize = 13,
            BufferSize = 5000,
        };
        _terminal.ProcessExited += OnProcessExited;

        // "Prompt from Perch": writes the text into the same PTY (as if typed), so the interactive TUI
        // receives it and Enter submits it. The terminal above stays fully usable for direct typing.
        _promptBox = new TextBox
        {
            PlaceholderText = "Prompt from Perch → types into the terminal (Enter to send)",
            FontSize = 13, IsEnabled = false, VerticalAlignment = VerticalAlignment.Center,
        };
        _promptBox.KeyDown += OnPromptKeyDown;
        _sendButton = new Button
        {
            Content = "Send", FontSize = 12, CornerRadius = new CornerRadius(6), IsEnabled = false,
            Margin = new Thickness(8, 0, 0, 0),
        };
        _sendButton.Click += (_, _) => SendPrompt();
        var inputRow = new DockPanel { Margin = new Thickness(12, 8, 12, 12), Children = { _sendButton, _promptBox } };
        _sendButton[DockPanel.DockProperty] = Dock.Right;
        inputRow[DockPanel.DockProperty] = Dock.Bottom;

        var terminalHost = new Border
        {
            Margin = new Thickness(12, 4, 12, 0), CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Palette.Sunken), ClipToBounds = true, Child = _terminal,
        };

        Content = new DockPanel { Children = { toolbarPanel, inputRow, terminalHost } };
    }

    /// <summary>Opens the terminal already targeting a session to resume by id (the elevation
    /// destination): the working directory is set and <c>claude --resume &lt;id&gt;</c> launches on open.</summary>
    public void ResumeSession(string sessionId, string cwd)
    {
        _resumeId = sessionId;
        if (Directory.Exists(cwd)) _cwdBox.Text = cwd;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (!_launched) Launch();   // auto-start (fresh or resume) so the window is useful immediately
    }

    private void Launch()
    {
        if (_launched && _terminal.IsLive) return;
        var cwd = _cwdBox.Text?.Trim() ?? "";
        if (!Directory.Exists(cwd))
        {
            _statusLabel.Text = $"folder not found: {cwd}";
            return;
        }

        // `claude` is a .cmd shim on Windows PATH, so it needs a shell host; on Unix it's a plain exec.
        // Running it as the PTY's child means claude's TUI owns the pseudo-console directly, and its exit
        // ends the session cleanly (ProcessExited).
        var args = new List<string>();
        string process;
        if (OperatingSystem.IsWindows())
        {
            process = "cmd.exe";
            args.Add("/c");
            args.Add("claude");
        }
        else
        {
            process = "claude";
        }
        if (!string.IsNullOrEmpty(_resumeId))
        {
            args.Add("--resume");
            args.Add(_resumeId);
        }

        try
        {
            _terminal.LaunchProcess(cwd, process, args.ToArray());
        }
        catch (Exception ex)
        {
            _statusLabel.Text = $"failed to launch: {ex.Message}";
            return;
        }

        _launched = true;
        _statusLabel.Text = _resumeId is null ? "running" : $"resumed {Shorten(_resumeId)}";
        _startButton.IsEnabled = false;
        _promptBox.IsEnabled = true;
        _sendButton.IsEnabled = true;
        _terminal.Focus();
    }

    private void OnProcessExited(object? sender, ProcessExitedEventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        _statusLabel.Text = $"claude exited ({e.ExitCode})";
        _launched = false;
        _startButton.IsEnabled = true;
        _startButton.Content = "Restart claude";
        _promptBox.IsEnabled = false;
        _sendButton.IsEnabled = false;
    });

    private void OnPromptKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            SendPrompt();
            e.Handled = true;
        }
    }

    private void SendPrompt()
    {
        var text = _promptBox.Text?.Trim();
        if (string.IsNullOrEmpty(text) || !_terminal.IsLive) return;
        _promptBox.Text = "";
        // Verbatim + carriage return submits, exactly as typing the prompt and pressing Enter would.
        _ = _terminal.SendInputAsync(text + "\r", default);
        _terminal.Focus();
    }

    private static string Shorten(string id) => id.Length > 8 ? id[..8] : id;

    protected override void OnClosed(EventArgs e)
    {
        try { _terminal.ProcessExited -= OnProcessExited; } catch { }
        // Disposing the control tears down the PTY + its child process tree.
        try { (_terminal as IDisposable)?.Dispose(); } catch { }
        base.OnClosed(e);
    }
}

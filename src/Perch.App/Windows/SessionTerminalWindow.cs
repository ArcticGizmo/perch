using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
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
    private readonly Button _browseButton;
    private readonly Button _startButton;
    private readonly TextBlock _statusLabel;
    private readonly TerminalControl _terminal;
    private readonly TextBox _promptBox;
    private readonly Button _sendButton;

    private string? _resumeId;
    private bool _launched;
    private bool _autoLaunchOnLoad;   // resume/elevation: launch once the terminal is laid out (sized)

    public SessionTerminalWindow()
    {
        Title = "Session terminal (PoC)";
        Width = 900;
        Height = 640;
        MinWidth = 560;
        MinHeight = 380;
        Background = Palette.SurfaceSunkenBrush;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        // Deliberately empty: a session must be pointed at a project explicitly — launching at the home
        // dir (or a filesystem root) is a footgun, so Start stays disabled until a real folder is chosen.
        _cwdBox = new TextBox
        {
            Text = "",
            PlaceholderText = "Select a project folder…", FontSize = 12, MinWidth = 300,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _cwdBox.TextChanged += (_, _) => UpdateStartEnabled();
        _browseButton = new Button
        {
            Content = "📁", FontSize = 13, CornerRadius = new CornerRadius(6),
            VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(8, 4),
            [ToolTip.TipProperty] = "Choose a project folder…",
        };
        _browseButton.Click += async (_, _) => await BrowseAsync();
        _startButton = new Button
        {
            Content = "Start claude", FontSize = 12, CornerRadius = new CornerRadius(6),
            VerticalAlignment = VerticalAlignment.Center, IsEnabled = false,
        };
        _startButton.Click += (_, _) => Launch();
        _statusLabel = new TextBlock
        {
            Text = "select a folder to begin", Foreground = Palette.MutedBrush, FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(12, 9),
            Children = { _cwdBox, _browseButton, _startButton, _statusLabel },
        };
        var toolbarPanel = new Border { Background = Palette.FormBgBrush, Child = toolbar, [DockPanel.DockProperty] = Dock.Top };

        _terminal = new TerminalControl
        {
            FontFamily = Mono,
            FontSize = 13,
            BufferSize = 5000,
            // Empty so the control does NOT auto-launch its default shell (cmd.exe) on load — otherwise it
            // spawns a phantom shell AND our explicit LaunchProcess runs a second process, which is what
            // produced the duplicated/reflowed frames. We own every launch via LaunchProcess(cwd, …).
            Process = "",
        };
        _terminal.ProcessExited += OnProcessExited;
        _terminal.Loaded += OnTerminalLoaded;   // defers a resumed session's launch until the control is sized

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
    /// destination): the working directory is set and <c>claude --resume &lt;id&gt;</c> auto-launches once the
    /// terminal has been laid out (see <see cref="OnTerminalLoaded"/>). A fresh session never auto-launches
    /// — the user must pick a folder and press Start.</summary>
    public void ResumeSession(string sessionId, string cwd)
    {
        _resumeId = sessionId;
        if (Directory.Exists(cwd)) _cwdBox.Text = cwd;
        _autoLaunchOnLoad = !string.IsNullOrEmpty(_resumeId) && Directory.Exists(cwd);
    }

    // Auto-launch a resumed session only after the terminal control is laid out and has a real size —
    // launching before that starts the PTY at a default/tiny size, and claude repaints after the resize,
    // leaving a duplicated frame in scrollback (the reflow artifact). Fresh sessions launch on the Start
    // click, which is always after layout, so they're unaffected.
    private void OnTerminalLoaded(object? sender, RoutedEventArgs e)
    {
        if (_autoLaunchOnLoad && !_launched)
        {
            _autoLaunchOnLoad = false;
            Launch();
        }
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
        _cwdBox.IsEnabled = false;       // the cwd is fixed once claude is running
        _browseButton.IsEnabled = false;
        _promptBox.IsEnabled = true;
        _sendButton.IsEnabled = true;
        _terminal.Focus();
    }

    private void OnProcessExited(object? sender, ProcessExitedEventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        _statusLabel.Text = $"claude exited ({e.ExitCode})";
        _launched = false;
        _startButton.Content = "Restart claude";
        _cwdBox.IsEnabled = true;
        _browseButton.IsEnabled = true;
        _promptBox.IsEnabled = false;
        _sendButton.IsEnabled = false;
        UpdateStartEnabled();
    });

    // Choose a project folder with the native OS folder picker (feedback: pick graphically, don't type).
    private async System.Threading.Tasks.Task BrowseAsync()
    {
        try
        {
            var start = Directory.Exists(_cwdBox.Text?.Trim())
                ? await StorageProvider.TryGetFolderFromPathAsync(_cwdBox.Text!.Trim())
                : null;
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Choose a project folder",
                AllowMultiple = false,
                SuggestedStartLocation = start,
            });
            if (folders.Count == 0) return;
            var path = folders[0].TryGetLocalPath();
            if (!string.IsNullOrEmpty(path)) _cwdBox.Text = path;   // TextChanged re-enables Start
        }
        catch (Exception ex)
        {
            _statusLabel.Text = $"couldn't open folder picker: {ex.Message}";
        }
    }

    // Start is enabled only when idle and the box holds a real folder — the guard against launching at
    // the home dir or a filesystem root.
    private void UpdateStartEnabled()
    {
        bool ok = !_launched && Directory.Exists(_cwdBox.Text?.Trim() ?? "");
        _startButton.IsEnabled = ok;
        if (!_launched)
            _statusLabel.Text = ok ? "ready — press Start" : "select a folder to begin";
    }

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

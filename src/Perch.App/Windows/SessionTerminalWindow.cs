using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Iciclecreek.Terminal;
using Perch.Avalonia.Theming;
using Perch.Data;

namespace Perch.Avalonia.Windows;

/// <summary>
/// The <b>terminal surface</b> and launcher for a Perch-controlled session (session-control — see
/// docs/session-control-poc.md §7). The genuine Claude Code TUI renders inside a Perch window via
/// <see cref="TerminalControl"/> (XTerm.NET over Porta.Pty), so the user keeps full terminal control.
/// A toolbar session picker starts a fresh session (default) or resumes an existing one; a "Prompt from
/// Perch" box writes into the same PTY.
///
/// <para>This window <em>owns the PTY</em> and exposes the session as a <see cref="SessionHost"/>
/// (<see cref="Host"/>). Other surfaces attach to that host: <see cref="OpenRichUiRequested"/> pops out a
/// terminal-free rich UI (<see cref="SessionUiWindow"/>) bound to the same session, so one live session
/// can be driven from the terminal <em>and</em> a rich desktop UI concurrently. It is also the elevation
/// destination — <c>--resume</c>ing an observed session takes over its transcript in-place.</para>
/// </summary>
internal sealed class SessionTerminalWindow : Window
{
    private static readonly FontFamily Mono = new("Cascadia Mono, Cascadia Code, Consolas, Menlo, monospace");

    private readonly ComboBox _sessionPicker;   // "New session" (default) or resume an existing one
    private readonly TextBox _cwdBox;
    private readonly Button _browseButton;
    private readonly Button _startButton;
    private readonly Button _openUiButton;
    private readonly TextBlock _statusLabel;
    private readonly TerminalControl _terminal;
    private readonly TextBox _promptBox;
    private readonly Button _sendButton;

    private string? _resumeId;
    private bool _launched;
    private bool _autoLaunchOnLoad;   // resume/elevation: launch once the terminal is laid out (sized)
    private bool _suppressPicker;
    private SessionHost? _session;

    /// <summary>The session this window is driving, once launched — the hub other surfaces attach to.</summary>
    public SessionHost? Host => _session;

    /// <summary>Raised when the user asks for the rich UI surface; the app opens a <see cref="SessionUiWindow"/>
    /// bound to the given host (a second, terminal-free client of the same live session).</summary>
    public event Action<SessionHost>? OpenRichUiRequested;

    // The synthetic first row of the session picker: selecting it means "start a fresh session".
    private static readonly HistoryEntry NewSessionEntry =
        new("", "✨  New session", "", "", DateTime.MaxValue, false) { IsPlaceholder = true };

    public SessionTerminalWindow()
    {
        Title = "Session terminal (PoC)";
        Width = 900;
        Height = 640;
        MinWidth = 560;
        MinHeight = 380;
        Background = Palette.SurfaceSunkenBrush;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        // Pick "New session" (default → a fresh id) or an existing session to resume. Choosing an existing
        // one fills in (and locks) its folder, so Start runs `claude --resume <id>` there.
        _sessionPicker = new ComboBox
        {
            MinWidth = 240, MaxDropDownHeight = 420, FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            ItemsSource = new List<HistoryEntry> { NewSessionEntry },
            SelectedIndex = 0,
            ItemTemplate = new FuncDataTemplate<HistoryEntry>((e, _) => new TextBlock
            {
                Text = e is null ? "" : PickerLabel(e),
            }, supportsRecycling: true),
        };
        _sessionPicker.SelectionChanged += (_, _) => OnPickerChanged();

        // Deliberately empty: a session must be pointed at a project explicitly — launching at the home
        // dir (or a filesystem root) is a footgun, so Start stays disabled until a real folder is chosen.
        _cwdBox = new TextBox
        {
            Text = "",
            PlaceholderText = "Select a project folder…", FontSize = 12, MinWidth = 260,
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
        _openUiButton = new Button
        {
            Content = "Open rich UI ▸", FontSize = 12, CornerRadius = new CornerRadius(6),
            VerticalAlignment = VerticalAlignment.Center, IsEnabled = false,
            [ToolTip.TipProperty] = "Open a rich desktop UI on this same session",
        };
        _openUiButton.Click += (_, _) => { if (_session is { } h) OpenRichUiRequested?.Invoke(h); };
        _statusLabel = new TextBlock
        {
            Text = "select a folder to begin", Foreground = Palette.MutedBrush, FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(12, 9),
            Children = { _sessionPicker, _cwdBox, _browseButton, _startButton, _openUiButton, _statusLabel },
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
        // Advertise full colour so claude emits its truecolor logo / dim styling rather than a reduced
        // palette: the default TERM is "xterm" (256-colour off), which some CLIs gate colour depth on
        // (Iciclecreek 4.x / XTerm.NET 2.x). Fidelity fixes for dim/palette/resize also come with the 4.x bump.
        if (_terminal.Options is { } termOptions) termOptions.TermName = "xterm-256color";
        (_terminal.EnvironmentVariables ??= new Dictionary<string, string>())["COLORTERM"] = "truecolor";
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
        // Launch with a KNOWN id: resume an existing session, else pin a fresh GUID via --session-id so we
        // never have to guess which transcript is ours. Either way the host is seeded with the exact id.
        bool isResume = !string.IsNullOrEmpty(_resumeId);
        string launchId = isResume ? _resumeId! : Guid.NewGuid().ToString();
        if (isResume)
        {
            args.Add("--resume");
            args.Add(launchId);
        }
        else
        {
            args.Add("--session-id");
            args.Add(launchId);
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
        _startButton.IsEnabled = false;
        _cwdBox.IsEnabled = false;       // the cwd is fixed once claude is running
        _browseButton.IsEnabled = false;
        _sessionPicker.IsEnabled = false;
        _promptBox.IsEnabled = true;
        _sendButton.IsEnabled = true;
        _openUiButton.IsEnabled = true;  // the session hub now exists — a rich UI can attach to it

        // The session hub: the one PTY every surface writes into. It also tracks the active transcript
        // (following /resume, /clear, /rename) so this window's status — and any attached rich UI — stay
        // pointed at the session the terminal is really driving.
        _session = new SessionHost(_terminal, cwd, launchId, isResume);
        _session.ActiveSessionChanged += RefreshStatus;
        _session.TitleChanged += RefreshStatus;
        _session.Begin();
        RefreshStatus();

        _terminal.Focus();
    }

    private void OnProcessExited(object? sender, ProcessExitedEventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        _statusLabel.Text = $"claude exited ({e.ExitCode})";
        _launched = false;
        _startButton.Content = "Restart claude";
        _cwdBox.IsEnabled = true;
        _browseButton.IsEnabled = true;
        _sessionPicker.IsEnabled = true;
        _promptBox.IsEnabled = false;
        _sendButton.IsEnabled = false;
        _session?.Stop();
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
        if (string.IsNullOrEmpty(text)) return;
        _promptBox.Text = "";
        // Verbatim + carriage return submits, exactly as typing the prompt and pressing Enter would —
        // funnelled through the shared session so every surface uses the one PTY write path.
        _session?.SendText(text);
        _terminal.Focus();
    }

    // Reflect the session the terminal is really driving (id + /rename title, following /resume, /clear).
    private void RefreshStatus()
    {
        if (_session is not { } s) return;
        var id = s.SessionId is { Length: > 0 } sid ? Shorten(sid) : "?";
        var name = string.IsNullOrWhiteSpace(s.Title) ? "" : $" · “{s.Title}”";
        _statusLabel.Text = $"{(_resumeId is null ? "running" : "resumed")} {id}{name}";
    }

    // Picker label: the "New session" row, else the session's /rename title or project + when it last ran.
    private static string PickerLabel(HistoryEntry e) =>
        e.IsPlaceholder ? e.ProjectName : $"{e.DisplayName} · {e.RelativeTime}";

    // Choosing an existing session locks its folder in (resume must run in the session's own cwd);
    // choosing "New session" hands the folder controls back.
    private void OnPickerChanged()
    {
        if (_suppressPicker || _launched || _sessionPicker.SelectedItem is not HistoryEntry e) return;
        if (e.IsPlaceholder)
        {
            _resumeId = null;
            _cwdBox.IsEnabled = true;
            _browseButton.IsEnabled = true;
        }
        else
        {
            _resumeId = e.SessionId;
            _cwdBox.Text = e.Cwd;
            _cwdBox.IsEnabled = false;
            _browseButton.IsEnabled = false;
        }
        UpdateStartEnabled();
    }

    /// <summary>Fills the session picker with the recent resumable (non-active) sessions. Called by the app
    /// each time the window opens; ignored once a session is running so it never disturbs a live toolbar.</summary>
    public void SeedSessionPicker(IReadOnlySet<string> activeSessionIds)
    {
        if (_launched) return;
        System.Threading.Tasks.Task.Run(() => SessionHistory.ListAll(activeSessionIds)).ContinueWith(t =>
        {
            if (!t.IsCompletedSuccessfully) return;
            Dispatcher.UIThread.Post(() =>
            {
                if (!IsVisible || _launched) return;
                var items = new List<HistoryEntry> { NewSessionEntry };
                items.AddRange(t.Result
                    .Where(e => !e.IsActive && !string.IsNullOrEmpty(e.SessionId) && !string.IsNullOrEmpty(e.Cwd))
                    .Take(60));

                var keep = (_sessionPicker.SelectedItem as HistoryEntry)?.SessionId ?? "";
                _suppressPicker = true;
                _sessionPicker.ItemsSource = items;
                _sessionPicker.SelectedItem = items.FirstOrDefault(e => e.SessionId == keep) ?? items[0];
                _suppressPicker = false;
            });
        });
    }

    private static string Shorten(string id) => id.Length > 8 ? id[..8] : id;

    protected override void OnClosed(EventArgs e)
    {
        try { _terminal.ProcessExited -= OnProcessExited; } catch { }
        try { _session?.Stop(); } catch { }
        // Disposing the control tears down the PTY + its child process tree.
        try { (_terminal as IDisposable)?.Dispose(); } catch { }
        base.OnClosed(e);
    }
}

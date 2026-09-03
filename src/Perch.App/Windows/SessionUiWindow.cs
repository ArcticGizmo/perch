using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Perch.Avalonia.Theming;
using Perch.Avalonia.Views;

namespace Perch.Avalonia.Windows;

/// <summary>
/// The <b>rich UI surface</b> for a Perch-controlled session (session-control — see
/// docs/session-control-poc.md §7): a Claude-Desktop-style window that visualises a session and drives it,
/// with <em>no terminal in it</em>. It's a client of a <see cref="SessionHost"/> owned by a
/// <see cref="SessionTerminalWindow"/> — it renders that session's transcript live
/// (<see cref="TranscriptReadableView"/>) and its prompt box injects into the same PTY via
/// <see cref="SessionHost.SendText"/>. So the one live session is driven from the terminal <em>and</em>
/// this UI, concurrently (turn by turn). It follows <c>/resume</c>, <c>/clear</c> and <c>/rename</c> via
/// the host's <see cref="SessionHost.ActiveSessionChanged"/> / <see cref="SessionHost.TitleChanged"/>.
/// </summary>
internal sealed class SessionUiWindow : Window
{
    private readonly SessionHost _host;
    private readonly TranscriptReadableView _readable;
    private readonly TextBlock _status;
    private readonly TextBox _promptBox;
    private readonly Button _sendButton;

    public SessionUiWindow(SessionHost host)
    {
        _host = host;
        Title = "Session UI (PoC)";
        Width = 720;
        Height = 640;
        MinWidth = 420;
        MinHeight = 360;
        Background = Palette.SurfaceSunkenBrush;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _status = new TextBlock
        {
            Text = "attaching…", Foreground = Palette.MutedBrush, FontSize = 11,
            Margin = new Thickness(12, 9), TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var header = new Border { Background = Palette.FormBgBrush, Child = _status, [DockPanel.DockProperty] = Dock.Top };

        _readable = new TranscriptReadableView();
        var readableHost = new Border
        {
            Margin = new Thickness(12, 8, 12, 0), CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Palette.Sunken), ClipToBounds = true, Child = _readable,
        };

        _promptBox = new TextBox
        {
            PlaceholderText = "Prompt from Perch UI → same session (Enter to send)",
            FontSize = 13, VerticalAlignment = VerticalAlignment.Center,
        };
        _promptBox.KeyDown += OnPromptKeyDown;
        _sendButton = new Button
        {
            Content = "Send", FontSize = 12, CornerRadius = new CornerRadius(6),
            Margin = new Thickness(8, 0, 0, 0),
        };
        _sendButton.Click += (_, _) => SendPrompt();
        var inputRow = new DockPanel { Margin = new Thickness(12, 8, 12, 12), Children = { _sendButton, _promptBox } };
        _sendButton[DockPanel.DockProperty] = Dock.Right;
        inputRow[DockPanel.DockProperty] = Dock.Bottom;

        Content = new DockPanel { Children = { header, inputRow, readableHost } };
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _host.ActiveSessionChanged += OnActiveSessionChanged;
        _host.TitleChanged += OnActiveSessionChanged;
        OnActiveSessionChanged();   // attach to whatever the host is already pointed at
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        base.OnKeyDown(e);
    }

    // The host's active transcript (or its /rename title) changed — re-point the render and relabel.
    private void OnActiveSessionChanged()
    {
        if (!string.IsNullOrEmpty(_host.TranscriptPath)) _readable.Attach(_host.TranscriptPath!);

        var id = _host.SessionId is { Length: > 0 } sid ? Shorten(sid) : "?";
        var name = string.IsNullOrWhiteSpace(_host.Title) ? "" : $" · “{_host.Title}”";
        var live = _host.IsLive ? "following (same session as the terminal)" : "session ended";
        _status.Text = $"session {id}{name} · {live}";
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
        if (!_host.IsLive) { _status.Text = "session ended — can't send"; return; }
        _promptBox.Text = "";
        _host.SendText(text);   // same PTY as the terminal — the concurrent multi-surface proof
    }

    private static string Shorten(string id) => id.Length > 8 ? id[..8] : id;

    protected override void OnClosed(EventArgs e)
    {
        try { _host.ActiveSessionChanged -= OnActiveSessionChanged; } catch { }
        try { _host.TitleChanged -= OnActiveSessionChanged; } catch { }
        try { _readable.Detach(); } catch { }
        base.OnClosed(e);
    }
}

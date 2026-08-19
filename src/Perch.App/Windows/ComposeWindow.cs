using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Perch.Avalonia.Theming;
using Perch.Social;

namespace Perch.Avalonia.Windows;

/// <summary>
/// A small composer for posting a status: a 280-character body, an optional mood emoji, a live counter and
/// Post / Cancel. Decoupled from the client via the <c>post</c> callback the App supplies (which posts, then
/// refreshes the feed). Ctrl+Enter posts, Esc cancels. Owned by the overlay and reused via
/// <see cref="WindowHost"/>.
/// </summary>
internal sealed class ComposeWindow : Window
{
    private const int MaxLen = 280;

    // A quick palette of common coding moods — click to set the mood without hunting for the OS emoji picker.
    private static readonly string[] MoodPalette =
        ["😌", "🔥", "🎉", "🚀", "🧠", "🐛", "☕", "😴", "🤔", "😅", "💥", "✅"];

    private readonly Func<string, string?, Task> _post;
    private readonly TextBox _body;
    private readonly TextBox _mood;
    private readonly TextBlock _counter;
    private readonly TextBlock _status;
    private readonly Button _postBtn;

    public ComposeWindow(Func<string, string?, Task> post, string? initialMood = null)
    {
        _post = post;
        Title = "Post a status";
        Width = 400;
        Height = 320;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Palette.FormBgBrush;

        _body = SettingsUi.ThemedTextArea("");
        _body.Watermark = "What are you working on?";
        _body.Height = 96;
        _body.TextChanged += (_, _) => UpdateCounter();

        _mood = SettingsUi.ThemedTextBox(initialMood ?? "");
        _mood.Watermark = "🙂";
        _mood.Width = 56;

        _counter = SettingsUi.FieldCaption(MaxLen.ToString());
        _status = SettingsUi.BodyText("");
        _postBtn = SettingsUi.FlatButton("Post");
        _postBtn.Click += async (_, _) => await DoPost();
        var cancel = SettingsUi.FlatButton("Cancel");
        cancel.Click += (_, _) => Close();

        var moodRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        moodRow.Children.Add(new TextBlock { Text = "Mood", Foreground = Palette.MutedBrush, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 });
        moodRow.Children.Add(_mood);
        moodRow.Children.Add(_counter);

        // A clickable emoji palette so setting a mood doesn't need the OS emoji picker; the freeform box above
        // still accepts anything. Selecting one fills the box (and clears if you re-tap the same one).
        var palette = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var emoji in MoodPalette)
        {
            var btn = new Button
            {
                Content = new TextBlock { Text = emoji, FontFamily = new FontFamily("Segoe UI Emoji"), FontSize = 16 },
                Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                Padding = new Thickness(6, 3), Margin = new Thickness(0, 0, 2, 2), Cursor = new Cursor(StandardCursorType.Hand),
            };
            btn.Click += (_, _) => { _mood.Text = _mood.Text == emoji ? "" : emoji; };
            palette.Children.Add(btn);
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel);
        buttons.Children.Add(_postBtn);

        var panel = new StackPanel { Margin = new Thickness(16), Spacing = 10 };
        panel.Children.Add(SettingsUi.SectionTitle("Post a status"));
        panel.Children.Add(_body);
        panel.Children.Add(moodRow);
        panel.Children.Add(palette);
        panel.Children.Add(_status);
        panel.Children.Add(buttons);
        Content = panel;

        UpdateCounter();
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
    }

    private void UpdateCounter()
    {
        int len = _body.Text?.Length ?? 0;
        int remaining = MaxLen - len;
        _counter.Text = remaining.ToString();
        _counter.Foreground = remaining < 0 ? new SolidColorBrush(Palette.Red) : Palette.MutedBrush;
        _postBtn.IsEnabled = remaining >= 0 && (_body.Text?.Trim().Length ?? 0) > 0;
    }

    private async Task DoPost()
    {
        var body = _body.Text?.Trim() ?? "";
        if (body.Length == 0) return;
        _postBtn.IsEnabled = false;
        _status.Text = "Posting…";
        try
        {
            var mood = string.IsNullOrWhiteSpace(_mood.Text) ? null : _mood.Text.Trim();
            await _post(body, mood);
            Close();
        }
        catch (SocialException ex) { _status.Text = ex.Message; }
        catch { _status.Text = "Couldn't post your status. Please try again."; }
        finally { _postBtn.IsEnabled = true; }
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control)) { _ = DoPost(); e.Handled = true; }
        else if (e.Key == Key.Escape) { Close(); e.Handled = true; }
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _body.Focus();
    }
}

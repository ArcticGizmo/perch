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
/// A small composer for posting a status: a mood button on the left (click for a searchable emoji picker), a
/// 280-character body, a live counter and Post / Cancel. Decoupled from the client via the <c>post</c> callback
/// the App supplies (which posts, then refreshes the feed). Ctrl+Enter posts, Esc cancels. Owned by the overlay
/// and reused via <see cref="WindowHost"/>.
/// </summary>
internal sealed class ComposeWindow : Window
{
    private const int MaxLen = 280;

    private readonly Func<string, string?, Task> _post;
    private readonly Func<IReadOnlyList<string>>? _recents;
    private readonly Action<string>? _onEmojiUsed;
    private readonly TextBox _body;
    private readonly Button _moodBtn;
    private readonly TextBlock _counter;
    private readonly TextBlock _status;
    private readonly Button _postBtn;

    private string? _mood;

    /// <param name="recents">Supplies the most-recently-used emoji for the mood picker's default grid, and
    /// <paramref name="onEmojiUsed"/> records a pick — the same wiring the overlay's reaction picker uses, so
    /// moods and reactions share one recents history and the same extended emoji search.</param>
    public ComposeWindow(Func<string, string?, Task> post, string? initialMood = null,
        Func<IReadOnlyList<string>>? recents = null, Action<string>? onEmojiUsed = null)
    {
        _post = post;
        _recents = recents;
        _onEmojiUsed = onEmojiUsed;
        _mood = string.IsNullOrWhiteSpace(initialMood) ? null : initialMood;
        Title = "Post a status";
        Width = 420;
        Height = 250;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Palette.FormBgBrush;

        _body = SettingsUi.ThemedTextArea("");
        _body.PlaceholderText = "What are you working on?";
        _body.Height = 96;
        _body.TextChanged += (_, _) => UpdateCounter();

        // Mood: a single button on the left showing the current mood; click opens the searchable picker.
        _moodBtn = new Button
        {
            Width = 40, Height = 40, Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            Background = Palette.ButtonBgBrush, BorderBrush = Palette.BorderBrush, BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Top, Cursor = new Cursor(StandardCursorType.Hand),
        };
        ToolTip.SetTip(_moodBtn, "Pick a mood");
        _moodBtn.Click += (_, _) => ShowMoodPicker();
        RefreshMoodButton();

        _counter = SettingsUi.FieldCaption(MaxLen.ToString());
        _status = SettingsUi.BodyText("");
        _postBtn = SettingsUi.FlatButton("Post");
        _postBtn.Click += async (_, _) => await DoPost();
        var cancel = SettingsUi.FlatButton("Cancel");
        cancel.Click += (_, _) => Close();

        // Mood button on the left, body to its right.
        var topRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        topRow.Children.Add(_moodBtn);
        var bodyCol = new StackPanel { Spacing = 4 };
        _body.Width = 340;
        bodyCol.Children.Add(_body);
        bodyCol.Children.Add(_counter);
        topRow.Children.Add(bodyCol);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel);
        buttons.Children.Add(_postBtn);

        var panel = new StackPanel { Margin = new Thickness(16), Spacing = 10 };
        panel.Children.Add(SettingsUi.SectionTitle("Post a status"));
        panel.Children.Add(topRow);
        panel.Children.Add(_status);
        panel.Children.Add(buttons);
        Content = panel;

        UpdateCounter();
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
    }

    private void RefreshMoodButton()
    {
        _moodBtn.Content = string.IsNullOrEmpty(_mood)
            ? new TextBlock { Text = "🙂", FontFamily = new FontFamily("Segoe UI Emoji"), FontSize = 18, Opacity = 0.45 }
            : new TextBlock { Text = _mood, FontFamily = new FontFamily("Segoe UI Emoji"), FontSize = 20 };
    }

    private void SetMood(string? mood)
    {
        _mood = string.IsNullOrWhiteSpace(mood) ? null : mood;
        RefreshMoodButton();
    }

    // The shared emoji picker (recents by default + the full cross-platform EmojiCatalog search), anchored just
    // below the mood button. showClear offers the "None" chip that clears the mood. Same picker the overlay's
    // reaction chooser uses, so every emoji selector in Perch behaves the same and shares one recents history.
    private void ShowMoodPicker()
    {
        var anchor = _moodBtn.PointToScreen(new Point(0, _moodBtn.Bounds.Height + 4));
        var picker = new EmojiPickerWindow(
            "Pick a mood",
            mood => SetMood(mood),          // null when the "clear" chip is used
            anchor,
            recents: _recents?.Invoke(),
            onEmojiUsed: _onEmojiUsed,
            showClear: true);
        picker.Show();
        picker.Activate();
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
            await _post(body, _mood);
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

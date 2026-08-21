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

    // A curated set of coding-ish moods with search keywords — enough that "search on click" is useful without
    // shipping a full emoji database. The OS emoji picker (Win + .) covers everything else while the box is focused.
    private static readonly (string Emoji, string Keywords)[] MoodData =
    [
        ("😌", "relieved calm chill relaxed content"), ("🔥", "fire lit hot streak on a roll"),
        ("🎉", "party celebrate ship done shipped"), ("🚀", "rocket launch ship fast"),
        ("🧠", "brain thinking focus deep smart"), ("🐛", "bug debug broken"),
        ("☕", "coffee tired break caffeine"), ("😴", "sleep tired sleepy zzz"),
        ("🤔", "thinking hmm pondering"), ("😅", "sweat nervous phew close"),
        ("💥", "boom crash blew up"), ("✅", "done check complete finished"),
        ("😎", "cool sunglasses confident"), ("😤", "determined grind pushing"),
        ("🥳", "party celebrate hooray"), ("😭", "crying sad rough"),
        ("🤯", "mind blown wow"), ("💀", "dead dying rip done for"),
        ("👀", "eyes looking watching reviewing"), ("🙃", "upside down irony chaos"),
        ("🏃", "running busy sprint"), ("🧹", "cleanup refactor tidy"),
        ("📦", "shipping release package deploy"), ("⚡", "fast energy quick"),
        ("🌙", "night late owl"), ("🎯", "focus goal target"),
        ("🤖", "ai bot automation agent"), ("🫠", "melting overwhelmed done"),
        ("😐", "meh neutral whatever"), ("🤬", "angry frustrated rage"),
        ("🥲", "smiling tear bittersweet"), ("🫡", "salute done sir yes"),
        ("🧪", "test experiment try"), ("🔧", "fix tools wrench"),
        ("📝", "writing notes docs planning"), ("🍕", "food lunch hungry"),
        ("💤", "sleep zzz afk away"), ("🎨", "design ui polish"),
        ("🧯", "firefighting incident oncall"), ("🌱", "new fresh start learning"),
    ];

    private readonly Func<string, string?, Task> _post;
    private readonly TextBox _body;
    private readonly Button _moodBtn;
    private readonly TextBlock _counter;
    private readonly TextBlock _status;
    private readonly Button _postBtn;

    private string? _mood;
    private Flyout? _moodFlyout;

    public ComposeWindow(Func<string, string?, Task> post, string? initialMood = null)
    {
        _post = post;
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
        ToolTip.SetTip(_moodBtn, "Pick a mood (or press Win + . in the box for the system emoji picker)");
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

    // A searchable emoji picker anchored to the mood button: a search box filtering the curated list by keyword,
    // a "None" option, and a hint that the OS emoji picker is a keystroke away.
    private void ShowMoodPicker()
    {
        var search = new TextBox { PlaceholderText = "search or type an emoji…", Width = 240 };
        var wrap = new WrapPanel { MaxWidth = 240 };

        void Choose(string? mood) { SetMood(mood); _moodFlyout?.Hide(); }

        void Rebuild(string q)
        {
            wrap.Children.Clear();

            // Typed/pasted a system emoji? Offer it as a highlighted "use this" chip so any mood is settable,
            // not just the curated set. Otherwise the "None" clear chip leads.
            if (EmojiText.ContainsEmoji(q))
            {
                var custom = EmojiText.FirstGrapheme(q);
                var chip = MoodChip(custom, clear: false);
                chip.BorderBrush = Palette.AccentBrush;
                chip.BorderThickness = new Thickness(1.5);
                chip.Click += (_, _) => Choose(custom);
                wrap.Children.Add(chip);
            }
            else
            {
                var none = MoodChip("🚫", clear: true);
                none.Click += (_, _) => Choose(null);
                wrap.Children.Add(none);
            }

            foreach (var (emoji, kw) in MoodData)
            {
                if (q.Length > 0 && !EmojiText.ContainsEmoji(q)
                    && !kw.Contains(q, StringComparison.OrdinalIgnoreCase) && emoji != q) continue;
                var b = MoodChip(emoji, clear: false);
                b.Click += (_, _) => Choose(emoji);
                wrap.Children.Add(b);
            }
        }
        Rebuild("");
        search.TextChanged += (_, _) => Rebuild(search.Text?.Trim() ?? "");
        // Enter commits a typed emoji directly (handy after Win + . drops one into the box).
        search.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            var q = search.Text?.Trim() ?? "";
            if (EmojiText.ContainsEmoji(q)) { Choose(EmojiText.FirstGrapheme(q)); e.Handled = true; }
        };

        var content = new StackPanel { Width = 260, Spacing = 8 };
        content.Children.Add(search);
        content.Children.Add(new ScrollViewer { MaxHeight = 200, Content = wrap });
        content.Children.Add(new TextBlock
        {
            Text = "Tip: type or paste any emoji above (Win + . opens the system picker), then Enter.",
            Foreground = Palette.MutedBrush, FontSize = 11, TextWrapping = TextWrapping.Wrap,
        });

        _moodFlyout = new Flyout { Content = content };
        _moodFlyout.ShowAt(_moodBtn);
        search.Focus();
    }

    private static Button MoodChip(string emoji, bool clear) => new()
    {
        Content = new TextBlock { Text = emoji, FontFamily = new FontFamily("Segoe UI Emoji"), FontSize = 16 },
        Background = Brushes.Transparent, BorderThickness = new Thickness(0),
        Padding = new Thickness(6, 3), Margin = new Thickness(0, 0, 2, 2),
        Cursor = new Cursor(StandardCursorType.Hand),
        Opacity = clear ? 0.6 : 1.0,
    };

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

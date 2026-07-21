using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Perch.Avalonia.Theming;
using Perch.Data;

namespace Perch.Avalonia.Windows;

/// <summary>
/// The post-update "what's new" card, centred on screen: a headline, a scrollable list of the changelog
/// sections released since the version that last ran here, and two buttons — Close, and a "Don't show
/// changelogs again" that suppresses future pop-ups via <see cref="_onSuppress"/>. Shown once per update
/// from the app startup check; the entries are picked by <see cref="ChangelogParser"/>. Styled off
/// <see cref="QrWindow"/> so the two popups read as one app.
/// </summary>
internal sealed class ChangelogWindow : Window
{
    private static readonly IBrush Bg     = new SolidColorBrush(Color.FromRgb(15, 15, 20));
    private static readonly IBrush Stroke = new SolidColorBrush(Color.FromRgb(45, 45, 60));
    private static readonly IBrush Fg     = new SolidColorBrush(Color.FromRgb(225, 225, 235));
    private static readonly IBrush Muted  = new SolidColorBrush(Color.FromRgb(120, 120, 140));

    private readonly Action _onSuppress;

    public ChangelogWindow(string headline, string subhead, IReadOnlyList<ChangelogSection> sections, Action onSuppress)
    {
        _onSuppress = onSuppress;

        WindowDecorations = WindowDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Topmost = true;
        CanResize = false;
        Width = 480;
        Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        Content = BuildCard(headline, subhead, sections);
    }

    private Control BuildCard(string headline, string subhead, IReadOnlyList<ChangelogSection> sections)
    {
        var title = new TextBlock
        {
            Text = headline, Foreground = Fg, FontWeight = FontWeight.Bold, FontSize = 16,
        };
        var sub = new TextBlock
        {
            Text = subhead, Foreground = Muted, FontSize = 12, Margin = new Thickness(0, 2, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        };
        var headingStack = new StackPanel { Children = { title, sub } };

        var closeGlyph = new Button
        {
            Content = "✕", Foreground = Muted, Background = Brushes.Transparent,
            BorderThickness = new Thickness(0), Padding = new Thickness(4, 0), FontSize = 14,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        closeGlyph.Click += (_, _) => Close();

        var header = new Grid { Children = { headingStack, closeGlyph } };

        var body = new StackPanel();
        if (sections.Count == 0)
        {
            body.Children.Add(SettingsUi.BodyText("No changelog entries in that range."));
        }
        for (int i = 0; i < sections.Count; i++)
        {
            if (i > 0) body.Children.Add(SettingsUi.Separator());
            ChangelogMarkdown.Render(body, sections[i].Block);
        }

        var scroller = new ScrollViewer
        {
            Content = body,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(0, 12, 0, 12),
        };

        var suppress = SettingsUi.FlatButton("Don't show changelogs again");
        suppress.Click += (_, _) => { try { _onSuppress(); } catch { } Close(); };

        var close = SettingsUi.FlatButton("Close");
        close.Background = Palette.AccentBrush;
        close.MinWidth = 84;
        close.Click += (_, _) => Close();

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { suppress, close },
        };

        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        Grid.SetRow(header, 0);
        Grid.SetRow(scroller, 1);
        Grid.SetRow(buttons, 2);
        grid.Children.Add(header);
        grid.Children.Add(scroller);
        grid.Children.Add(buttons);
        grid.Margin = new Thickness(22);

        return new Border
        {
            Background = Bg, CornerRadius = new CornerRadius(12),
            BorderBrush = Stroke, BorderThickness = new Thickness(1.5),
            Child = grid,
        };
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        base.OnKeyDown(e);
    }
}

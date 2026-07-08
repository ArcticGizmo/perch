using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Perch.Avalonia.Theming;
using Perch.Avalonia.Views;
using Perch.Data;

namespace Perch.Avalonia.Windows;

/// <summary>
/// The daily flight-path window (the Avalonia port of <c>FlightPathForm</c>). A toolbar steps between
/// days (‹ / › and a Today button; ← / → arrows do the same); the owner-drawn
/// <see cref="FlightPathTimeline"/> below it renders the day, scrolled. Reports come from
/// <see cref="FlightPathService"/>, computed off the UI thread.
/// </summary>
internal sealed class FlightPathWindow : Window
{
    private readonly FlightPathTimeline _timeline = new();
    private readonly Button _nextButton;
    private readonly Button _todayButton;
    private readonly TextBlock _dateLabel;
    private DateOnly _day = DateOnly.FromDateTime(DateTime.Now);

    public FlightPathWindow()
    {
        Title = "Flight path";
        Width = 900;
        Height = 620;
        MinWidth = 620;
        MinHeight = 460;
        Background = new SolidColorBrush(Color.FromRgb(18, 18, 24));
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var prev = NavButton("‹", () => StepDay(-1));
        _nextButton = NavButton("›", () => StepDay(+1));
        _dateLabel = new TextBlock
        {
            Foreground = new SolidColorBrush(Palette.Title), FontSize = 15, FontWeight = FontWeight.Bold,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0), MinWidth = 200,
        };
        _todayButton = new Button
        {
            Content = "Today", Width = 68, Height = 28, FontSize = 12, CornerRadius = new CornerRadius(6),
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
        };
        _todayButton.Click += (_, _) => GoToDay(DateOnly.FromDateTime(DateTime.Now));

        var left = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center,
            Children = { prev, _nextButton, _dateLabel },
        };
        var toolbar = new Grid { Margin = new Thickness(12, 9), Height = 46, Children = { left, _todayButton } };
        _todayButton.HorizontalAlignment = HorizontalAlignment.Right;
        _todayButton.VerticalAlignment = VerticalAlignment.Center;

        var toolbarPanel = new Border { Background = Palette.FormBgBrush, Child = toolbar, [DockPanel.DockProperty] = Dock.Top };
        var scroll = new ScrollViewer { Content = _timeline, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Content = new DockPanel { Children = { toolbarPanel, scroll } };
    }

    private static Button NavButton(string glyph, Action onClick)
    {
        var b = new Button
        {
            Content = glyph, Width = 36, Height = 28, FontSize = 15, CornerRadius = new CornerRadius(6),
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
        };
        b.Click += (_, _) => onClick();
        return b;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        RefreshPath();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape: Close(); e.Handled = true; break;
            case Key.Left:   StepDay(-1); e.Handled = true; break;
            case Key.Right:  StepDay(+1); e.Handled = true; break;
        }
        base.OnKeyDown(e);
    }

    private void StepDay(int delta) => GoToDay(_day.AddDays(delta));

    private void GoToDay(DateOnly day)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (day > today) day = today;   // no future to show
        if (day == _day) return;
        _day = day;
        RefreshPath();
    }

    private void UpdateNavState()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        _dateLabel.Text = _day == today ? "Today"
            : _day.Year == today.Year ? _day.ToString("dddd, MMM d") : _day.ToString("MMM d, yyyy");
        _nextButton.IsEnabled = _day < today;
        _todayButton.IsEnabled = _day != today;
    }

    private void RefreshPath()
    {
        UpdateNavState();
        _timeline.SetLoading();

        var day = _day;
        System.Threading.Tasks.Task.Run(() => FlightPathService.ForDay(day)).ContinueWith(t =>
        {
            if (!t.IsCompletedSuccessfully) return;
            Dispatcher.UIThread.Post(() =>
            {
                if (!IsVisible || _day != day) return; // a newer day may have been requested meanwhile
                _timeline.SetReport(t.Result);
            });
        });
    }
}

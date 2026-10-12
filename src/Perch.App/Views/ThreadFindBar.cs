using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Perch.Avalonia.Theming;
using Perch.Data.Control;

namespace Perch.Avalonia.Views;

/// <summary>
/// The Ctrl+F find bar over a <see cref="SessionThreadView"/>: a query box, an "n/m" counter, prev/next and close,
/// pinned top-right over the thread. It drives the thread's search (every hit washed, the current one brighter and
/// scrolled into view). Enter / Shift+Enter step; Esc or ✕ close. Shared by the live session window and the history
/// viewer, which also opens it pre-filled when landing on a transcript-search hit.
/// </summary>
internal sealed class ThreadFindBar : Border
{
    private readonly SessionThreadView _thread;
    private readonly TextBox _box;
    private readonly TextBlock _count;
    private bool _quiet;   // a programmatic Text set that shouldn't jump to the first match

    /// <summary>Raised after the bar closes (✕, Esc), so the host can put focus back where it belongs.</summary>
    public event Action? Closed;

    public ThreadFindBar(SessionPalette p, SessionThreadView thread)
    {
        _thread = thread;
        _box = new TextBox
        {
            FontFamily = p.Body, FontSize = 13, Foreground = p.Text, Width = 220,
            PlaceholderText = "Find in conversation", Background = Brushes.Transparent,
            BorderThickness = new Thickness(0), Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center,
        };
        _box.TextChanged += (_, _) => { if (!_quiet) Run(jumpToFirst: true); };
        _box.AddHandler(KeyDownEvent, OnBoxKeyDown, RoutingStrategies.Tunnel);

        _count = new TextBlock
        {
            FontFamily = p.Mono, FontSize = 11.5, Foreground = p.Faint, MinWidth = 46,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var prev = new SessionButton(p, "↑", SessionButtonKind.Quiet, compact: true);
        prev.Click += () => Step(-1);
        var next = new SessionButton(p, "↓", SessionButtonKind.Quiet, compact: true);
        next.Click += () => Step(1);
        var close = new SessionButton(p, "✕", SessionButtonKind.Quiet, compact: true);
        close.Click += Close;

        IsVisible = false;
        Background = p.Raised;
        BorderBrush = p.Border;
        BorderThickness = new Thickness(1);
        CornerRadius = SessionPalette.ButtonRadius;
        Padding = new Thickness(12, 7);
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Top;
        Margin = new Thickness(0, 12, 20, 0);
        BoxShadow = BoxShadows.Parse("0 10 30 0 #55000000");
        Child = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center,
            Children = { _box, _count, prev, next, close },
        };
    }

    /// <summary>Open the bar, focus the box with its text selected, and re-run the query.</summary>
    public void Open()
    {
        IsVisible = true;
        _box.Focus();
        _box.SelectAll();
        Run(jumpToFirst: true);
    }

    /// <summary>Close the bar and clear the thread's highlighting.</summary>
    public void Close()
    {
        if (!IsVisible) return;
        IsVisible = false;
        _thread.ClearSearch();
        Closed?.Invoke();
    }

    /// <summary>Open the bar on <paramref name="query"/> with the first hit in or after <paramref name="near"/>
    /// current (the history viewer landing on a transcript-search hit), rather than the thread's first. Returns
    /// the number of matches; with none, the caller can still bring <paramref name="near"/> into view.</summary>
    public int ShowQuery(string query, ConversationItem? near)
    {
        IsVisible = true;
        _quiet = true;
        try { _box.Text = query; } finally { _quiet = false; }
        int n = _thread.Search(query);
        if (n > 0) _thread.ShowMatch(_thread.FirstMatchFrom(near));
        UpdateCount();
        _box.Focus();   // so Enter / Shift+Enter step on from the landing
        return n;
    }

    // Re-run the query, wash the matches, update the counter, and optionally jump to the first hit.
    private void Run(bool jumpToFirst)
    {
        int n = _thread.Search(_box.Text ?? "");
        if (n > 0 && jumpToFirst) _thread.ShowMatch(0);
        UpdateCount();
    }

    // Step to the next (+1) / previous (-1) match, wrapping around.
    private void Step(int delta)
    {
        if (_thread.MatchCount > 0)
        {
            int cur = _thread.CurrentMatch;
            _thread.ShowMatch(cur < 0 ? 0 : cur + delta);
        }
        UpdateCount();
    }

    private void UpdateCount()
    {
        int n = _thread.MatchCount;
        _count.Text = string.IsNullOrWhiteSpace(_box.Text) ? ""
            : n == 0 ? "0/0"
            : $"{_thread.CurrentMatch + 1}/{n}";
    }

    // Enter / Shift+Enter step through matches; Esc closes — kept off the host's own keys by handling here.
    private void OnBoxKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                Step(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
                e.Handled = true;
                break;
            case Key.Escape:
                Close();
                e.Handled = true;
                break;
        }
    }
}

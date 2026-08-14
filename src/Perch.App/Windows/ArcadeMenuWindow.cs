using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Perch.Avalonia.Rendering;
using Perch.Avalonia.Theming;

namespace Perch.Avalonia.Windows;

/// <summary>
/// The little "Perch Arcade" chooser that now stands between the ten-clicks easter egg and the two secret
/// toys (<see cref="SpaceInvadersWindow"/> and <see cref="FroggerWindow"/>). Pick a game with the arrow keys
/// (or the mouse) and Enter/Space launches it; the chooser closes as it hands off. Owner-drawn over the same
/// <see cref="OverlayDraw"/> / <see cref="Palette"/> vocabulary as the games themselves.
/// </summary>
public sealed class ArcadeMenuWindow : Window
{
    private readonly ArcadeMenu _menu = new();

    public ArcadeMenuWindow(Action launchInvaders, Action launchFrogger)
    {
        Title = "Perch Arcade";
        CanResize = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Palette.OverlaySurfaceBrush;
        Content = _menu;

        _menu.Chosen += index =>
        {
            Close();
            (index == 0 ? launchInvaders : launchFrogger)();
        };
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _menu.Focus();
        _menu.Begin();
    }

    protected override void OnClosed(EventArgs e)
    {
        _menu.Stop();
        base.OnClosed(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); e.Handled = true; return; }
        if (_menu.HandleKey(e.Key)) e.Handled = true;
        base.OnKeyDown(e);
    }
}

/// <summary>The chooser's owner-drawn control: a title, two selectable game cards each with a tiny live
/// sprite, and a shimmering prompt. Raises <see cref="Chosen"/> with the selected index (0 = Invaders,
/// 1 = Crossing).</summary>
internal sealed class ArcadeMenu : Control
{
    private const double MenuW = 460, MenuH = 400;
    private const double CardX = 40, CardW = MenuW - 2 * CardX, CardH = 92, CardGap = 20;
    private const double FirstCardY = 130;
    private const int TickMs = 16;

    public event Action<int>? Chosen;

    private static readonly (string Title, string Blurb)[] Games =
    {
        ("PERCH INVADERS", "Blast the descending swarm"),
        ("PERCH CROSSING", "Hop the bird home, Frogger-style"),
    };

    private int _selected;
    private double _pulse;
    private double _anim;                 // drives the little demo sprites
    private DispatcherTimer? _timer;

    // A classic invader (11×8) and the crossing bird (11×11), reused as the card icons.
    private static readonly string[] Invader =
    {
        "00100000100",
        "00010001000",
        "00111111100",
        "01101110110",
        "11111111111",
        "10111111101",
        "10100000101",
        "00011011000",
    };
    private static readonly string[] Bird =
    {
        "00000100000",
        "00001110000",
        "00011111000",
        "01011111010",
        "11111111111",
        "11111111111",
        "01111111110",
        "00111111100",
        "00111111100",
        "00110101100",
        "00100000100",
    };

    public ArcadeMenu()
    {
        Width = MenuW;
        Height = MenuH;
        Focusable = true;
    }

    protected override Size MeasureOverride(Size availableSize) => new(MenuW, MenuH);

    public void Begin()
    {
        _timer ??= CreateTimer();
        if (!_timer.IsEnabled) _timer.Start();
    }

    public void Stop() => _timer?.Stop();

    private DispatcherTimer CreateTimer()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(TickMs) };
        t.Tick += (_, _) => { _pulse += 0.12; _anim += 0.06; InvalidateVisual(); };
        return t;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _timer?.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    public bool HandleKey(Key key)
    {
        switch (key)
        {
            case Key.Up or Key.W or Key.Left or Key.A:
                _selected = (_selected + Games.Length - 1) % Games.Length; InvalidateVisual(); return true;
            case Key.Down or Key.S or Key.Right or Key.D:
                _selected = (_selected + 1) % Games.Length; InvalidateVisual(); return true;
            case Key.Enter or Key.Space:
                Chosen?.Invoke(_selected); return true;
        }
        return false;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        int hit = CardAt(e.GetPosition(this));
        if (hit >= 0 && hit != _selected) { _selected = hit; InvalidateVisual(); }
        base.OnPointerMoved(e);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        int hit = CardAt(e.GetPosition(this));
        if (hit >= 0) { _selected = hit; Chosen?.Invoke(hit); e.Handled = true; }
        base.OnPointerPressed(e);
    }

    private static int CardAt(Point p)
    {
        for (int i = 0; i < Games.Length; i++)
        {
            double y = FirstCardY + i * (CardH + CardGap);
            if (new Rect(CardX, y, CardW, CardH).Contains(p)) return i;
        }
        return -1;
    }

    public override void Render(DrawingContext ctx)
    {
        double w = Bounds.Width, h = Bounds.Height;
        ctx.FillRectangle(Palette.OverlaySurfaceBrush, new Rect(0, 0, w, h));

        var title = OverlayDraw.Text("PERCH ARCADE", 28, Palette.AccentBrush, FontWeight.Bold);
        ctx.DrawText(title, new Point((w - title.Width) / 2, 46));
        var sub = OverlayDraw.Text("Choose your game", 13, Palette.MutedBrush);
        ctx.DrawText(sub, new Point((w - sub.Width) / 2, 86));

        for (int i = 0; i < Games.Length; i++)
            DrawCard(ctx, i);

        double a = 0.45 + 0.55 * Math.Abs(Math.Sin(_pulse));
        using (ctx.PushOpacity(a))
        {
            var hint = OverlayDraw.Text("Arrow keys select  ·  Enter to play", 13, Palette.FgBrush, FontWeight.Bold);
            ctx.DrawText(hint, new Point((w - hint.Width) / 2, h - 56));
        }
        var esc = OverlayDraw.Text("Esc to close", 11, Palette.MutedBrush);
        ctx.DrawText(esc, new Point((w - esc.Width) / 2, h - 30));
    }

    private void DrawCard(DrawingContext ctx, int i)
    {
        double y = FirstCardY + i * (CardH + CardGap);
        var rect = new Rect(CardX, y, CardW, CardH);
        bool sel = i == _selected;

        var fill = sel ? Palette.OverlayRowHoverBrush : Palette.SurfaceSunkenBrush;
        var border = sel ? new Pen(Palette.AccentBrush, 2) : new Pen(Palette.BorderBrush, 1);
        OverlayDraw.Panel(ctx, rect, fill, border, 10);

        // Icon tile on the left, with a small bob when selected.
        double bob = sel ? 2.0 * Math.Sin(_anim * 2) : 0;
        var icon = new Rect(CardX + 22, y + (CardH - 44) / 2 + bob, 44, 44);
        if (i == 0)
            DrawBitSprite(ctx, Invader, icon, Palette.RunningBrush);
        else
            DrawBitSprite(ctx, Bird, icon, Palette.AccentBrush);

        double tx = CardX + 88;
        var name = OverlayDraw.Text(Games[i].Title, 18, Palette.FgBrush, FontWeight.Bold);
        ctx.DrawText(name, new Point(tx, y + 24));
        var blurb = OverlayDraw.Text(Games[i].Blurb, 12, Palette.MutedBrush);
        ctx.DrawText(blurb, new Point(tx, y + 52));
    }

    private static void DrawBitSprite(DrawingContext ctx, string[] rows, Rect box, IBrush brush)
    {
        int rN = rows.Length, cN = rows[0].Length;
        double cw = box.Width / cN, ch = box.Height / rN;
        for (int r = 0; r < rN; r++)
            for (int c = 0; c < cN; c++)
                if (rows[r][c] == '1')
                    ctx.FillRectangle(brush, new Rect(box.X + c * cw, box.Y + r * ch, cw + 0.5, ch + 0.5));
    }
}

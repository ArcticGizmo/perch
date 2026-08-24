using Avalonia;
using Avalonia.Media;
using Perch.Avalonia.Rendering;
using Perch.Avalonia.Theming;
using Perch.Games;
using Perch.Social;

namespace Perch.Avalonia.Views;

/// <summary>
/// The Connect 4 strip inside the social region: a compact row between the FRIENDS header and the first friend
/// row that shows one little disc icon per in-progress game, so you can see — and jump straight back into — a
/// game at a glance. The icon encodes two things: its <b>shape</b> tells a freshly-started ("requested") game
/// (a hollow ring) from one that's under way (a filled disc), and its <b>colour</b> tells whose move it is —
/// accent (with a small badge) when it's <em>your</em> turn, muted while you're waiting on your opponent.
/// Clicking an icon opens/continues that game. Games are fed by the same poll that drives the roster.
/// </summary>
public sealed partial class OverlayCanvas
{
    private IReadOnlyList<GameSummary> _games = [];
    private Guid _gamesMeId;
    private const int MaxGameIcons = 8;

    private readonly List<(Rect Rect, GameSummary Game)> _gameIconRects = new();
    private int _hoveredGameIcon = -1;

    /// <summary>Raised when a game icon is clicked — the App opens/continues that game.</summary>
    public event Action<GameSummary>? GameOpenRequested;

    /// <summary>Feeds the signed-in user's Connect 4 games (only in-progress ones are shown) plus their own id
    /// (to work out whose turn it is). Relayouts when the strip appears/disappears or its height changes.</summary>
    public void SetGames(IReadOnlyList<GameSummary> games, Guid meId)
    {
        var active = games?.Where(g => g.Status == GameStatus.InProgress).ToList() ?? [];
        bool beforeVisible = GamesStripVisible;
        _games = active;
        _gamesMeId = meId;
        if (GamesStripVisible != beforeVisible) RemeasurePanel();
        else if (SocialRegionVisible) InvalidateVisual();
    }

    // Shown only in the expanded social region, and only when there's at least one active game.
    private bool GamesStripVisible => SocialRegionVisible && _regionExpanded && _games.Count > 0;

    // Icon row height, derived from the caption line height (not a magic pixel) so it scales with the font.
    private double GamesRowHeight => FeedCaptionHeight + 16;

    // Paints the strip at y=top (its height is reserved by SocialRegionHeight): a small "GAMES" caption then a
    // disc per game, capped with a "+N" overflow.
    private void DrawGamesStrip(DrawingContext ctx, double width, double top)
    {
        double rowH = GamesRowHeight;
        double midY = top + rowH / 2;

        var cap = OverlayDraw.Text("GAMES", FeedCaptionSize, MutedBrush);
        OverlayDraw.TextLeftMid(ctx, cap, HorizPad + 2, midY);
        double x = HorizPad + 2 + cap.Width + 12;

        double d = FeedCaptionHeight + 4;   // disc diameter
        double gap = 9;
        int shown = Math.Min(_games.Count, MaxGameIcons);
        for (int i = 0; i < shown; i++)
        {
            var g = _games[i];
            var iconRect = new Rect(x, midY - d / 2, d, d);
            var hitRect = iconRect.Inflate(3);
            if (_hoveredGameIcon == i) OverlayDraw.Panel(ctx, hitRect, FeedHoverBrush, null, 6);
            DrawGameIcon(ctx, iconRect.Center, d, g);
            _gameIconRects.Add((hitRect, g));
            x += d + gap;
        }
        if (_games.Count > shown)
        {
            var moreFt = OverlayDraw.Text($"+{_games.Count - shown}", FeedCaptionSize, MutedBrush);
            OverlayDraw.TextLeftMid(ctx, moreFt, x, midY);
        }
    }

    // One game's disc: hollow ring = a freshly-started ("requested") game, filled = in play; accent when it's
    // your move (plus a small attention badge so it reads as "needs you"), muted while waiting on the opponent.
    private void DrawGameIcon(DrawingContext ctx, Point c, double d, GameSummary g)
    {
        bool myTurn = g.IsTurnOf(_gamesMeId);
        bool requested = g.MoveCount == 0;   // no moves yet reads as a new / just-requested game
        var brush = myTurn ? Palette.AccentBrush : MutedBrush;
        double r = d / 2 - 1;

        if (requested) ctx.DrawEllipse(null, new Pen(brush, 2), c, r, r);   // ring = new / requested
        else ctx.DrawEllipse(brush, null, c, r, r);                         // filled = under way

        if (myTurn)
            ctx.DrawEllipse(new SolidColorBrush(AttentionColor), null, new Point(c.X + r * 0.72, c.Y - r * 0.72), 2.6, 2.6);
    }

    private int HitTestGameIcon(Point p)
    {
        for (int i = 0; i < _gameIconRects.Count; i++) if (_gameIconRects[i].Rect.Contains(p)) return i;
        return -1;
    }

    // Dwell tooltip for a game icon: opponent, state (new / in play), and whose turn — wired via TipKind.Game.
    private void ShowGameTooltip(int index)
    {
        if (index < 0 || index >= _gameIconRects.Count) return;
        var (rect, g) = _gameIconRects[index];
        var opp = g.Opponent(_gamesMeId);
        string state = g.MoveCount == 0 ? "new game" : "in play";
        string turn = g.IsTurnOf(_gamesMeId) ? "your turn" : $"waiting for @{opp?.Handle}";
        Tooltip().ShowLines(
            [new($"Connect 4 vs @{opp?.Handle ?? "?"}  ·  {state}  ·  {turn}", OverlayTooltip.FgColor, false)],
            ToScreen(rect.Left, rect.Bottom + 4));
    }
}

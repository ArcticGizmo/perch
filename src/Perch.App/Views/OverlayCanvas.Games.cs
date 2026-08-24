using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Perch.Avalonia.Rendering;
using Perch.Avalonia.Theming;
using Perch.Games;
using Perch.Social;

namespace Perch.Avalonia.Views;

/// <summary>
/// The Connect 4 strip inside the social region: a compact "GAMES" row between the FRIENDS header and the first
/// friend row, with one little disc icon per pending invite or in-progress game, so you can see — and act on —
/// a game at a glance. The icon encodes two things:
/// <list type="bullet">
///   <item><b>shape</b>: a hollow ring for a <em>requested</em> game (an invite not yet accepted), a filled disc
///     for one that's under way;</item>
///   <item><b>colour</b>: accent (with a small attention badge) when it needs <em>you</em> — an invite waiting
///     on you to accept, or a game where it's your move — and muted while you're waiting on your opponent.</item>
/// </list>
/// Clicking an in-progress game opens/continues it; clicking a request pops an Accept/Decline (or Cancel) menu.
/// Games and requests ride the existing feed poll.
/// </summary>
public sealed partial class OverlayCanvas
{
    private IReadOnlyList<GameSummary> _games = [];
    private IReadOnlyList<GameRequest> _requests = [];
    private Guid _gamesMeId;
    private const int MaxGameIcons = 8;

    // One icon in the strip: exactly one of Game / Request is set.
    private readonly record struct GameStripItem(Rect Rect, GameSummary? Game, GameRequest? Request);
    private readonly List<GameStripItem> _gameIconRects = new();
    private int _hoveredGameIcon = -1;

    /// <summary>Raised when an in-progress game icon is clicked — the App opens/continues that game.</summary>
    public event Action<GameSummary>? GameOpenRequested;

    /// <summary>Raised when a game invite is acted on: accept (true) or decline/cancel (false).</summary>
    public event Action<GameRequest, bool>? GameRequestResponded;

    /// <summary>Feeds the signed-in user's in-progress games and pending invites, plus their own id (to work
    /// out whose turn it is / which invites are incoming). Relayouts when the strip appears or disappears.</summary>
    public void SetGames(IReadOnlyList<GameSummary> games, IReadOnlyList<GameRequest> requests, Guid meId)
    {
        var active = games?.Where(g => g.Status == GameStatus.InProgress).ToList() ?? [];
        bool beforeVisible = GamesStripVisible;
        _games = active;
        _requests = requests ?? [];
        _gamesMeId = meId;
        if (GamesStripVisible != beforeVisible) RemeasurePanel();
        else if (SocialRegionVisible) InvalidateVisual();
    }

    private bool HasGameStripItems => _games.Count + _requests.Count > 0;

    // Shown only in the expanded social region, and only when there's at least one invite or game.
    private bool GamesStripVisible => SocialRegionVisible && _regionExpanded && HasGameStripItems;

    // Icon row height, derived from the caption line height (not a magic pixel) so it scales with the font.
    private double GamesRowHeight => FeedCaptionHeight + 16;

    // Paints the strip at y=top (its height is reserved by SocialRegionHeight): a small "GAMES" caption, then a
    // disc per invite (first) and game, capped with a "+N" overflow.
    private void DrawGamesStrip(DrawingContext ctx, double width, double top)
    {
        double rowH = GamesRowHeight;
        double midY = top + rowH / 2;

        var cap = OverlayDraw.Text("GAMES", FeedCaptionSize, MutedBrush);
        OverlayDraw.TextLeftMid(ctx, cap, HorizPad + 2, midY);
        double x = HorizPad + 2 + cap.Width + 12;

        double d = FeedCaptionHeight + 4;   // disc diameter
        int total = _requests.Count + _games.Count;
        int budget = MaxGameIcons;
        int i = 0;

        // Invites first — they're the "requested" state and often need your action.
        foreach (var req in _requests)
        {
            if (budget <= 0) break;
            x = DrawStripIcon(ctx, x, midY, d, i++, requested: true, needsYou: req.IsIncoming(_gamesMeId),
                new GameStripItem(default, null, req));
            budget--;
        }
        foreach (var g in _games)
        {
            if (budget <= 0) break;
            x = DrawStripIcon(ctx, x, midY, d, i++, requested: false, needsYou: g.IsTurnOf(_gamesMeId),
                new GameStripItem(default, g, null));
            budget--;
        }

        if (total > MaxGameIcons)
        {
            var moreFt = OverlayDraw.Text($"+{total - MaxGameIcons}", FeedCaptionSize, MutedBrush);
            OverlayDraw.TextLeftMid(ctx, moreFt, x, midY);
        }
    }

    // Draws one disc at x (centred on midY), records its hit-rect, and returns the next x. Shape: ring for a
    // requested game, filled for one under way. Colour: accent + badge when it needs you, muted while waiting.
    private double DrawStripIcon(DrawingContext ctx, double x, double midY, double d, int index,
        bool requested, bool needsYou, GameStripItem item)
    {
        var center = new Point(x + d / 2, midY);
        var hit = new Rect(x, midY - d / 2, d, d).Inflate(3);
        if (_hoveredGameIcon == index) OverlayDraw.Panel(ctx, hit, FeedHoverBrush, null, 6);

        var brush = needsYou ? Palette.AccentBrush : MutedBrush;
        double r = d / 2 - 1;
        if (requested) ctx.DrawEllipse(null, new Pen(brush, 2), center, r, r);   // ring = invite / requested
        else ctx.DrawEllipse(brush, null, center, r, r);                         // filled = under way
        if (needsYou)
            ctx.DrawEllipse(new SolidColorBrush(AttentionColor), null,
                new Point(center.X + r * 0.72, center.Y - r * 0.72), 2.6, 2.6);

        _gameIconRects.Add(item with { Rect = hit });
        return x + d + 9;
    }

    private int HitTestGameIcon(Point p)
    {
        for (int i = 0; i < _gameIconRects.Count; i++) if (_gameIconRects[i].Rect.Contains(p)) return i;
        return -1;
    }

    // Routes a click on a strip icon: open an in-progress game, or pop the invite's Accept/Decline (Cancel) menu.
    private bool TryRouteGameIconClick(Point p)
    {
        foreach (var it in _gameIconRects)
        {
            if (!it.Rect.Contains(p)) continue;
            if (it.Game is { } g) GameOpenRequested?.Invoke(g);
            else if (it.Request is { } r) ShowGameRequestMenu(r);
            return true;
        }
        return false;
    }

    // Accept/Decline for an invite waiting on you; Cancel for one you sent.
    private void ShowGameRequestMenu(GameRequest r)
    {
        var items = new List<Control>();
        if (r.IsIncoming(_gamesMeId))
        {
            items.Add(MenuItem($"Accept game from @{r.Requester.Handle}", () => GameRequestResponded?.Invoke(r, true)));
            items.Add(MenuItem("Decline", () => GameRequestResponded?.Invoke(r, false)));
        }
        else
        {
            items.Add(MenuItem($"Waiting for @{r.Addressee.Handle} to accept", () => { }));
            items.Add(MenuItem("Cancel invite", () => GameRequestResponded?.Invoke(r, false)));
        }
        ShowFlyout(items);
    }

    // Dwell tooltip for a strip icon (wired via TipKind.Game): opponent, state, and whose move / whose accept.
    private void ShowGameTooltip(int index)
    {
        if (index < 0 || index >= _gameIconRects.Count) return;
        var it = _gameIconRects[index];
        string line;
        if (it.Request is { } r)
        {
            line = r.IsIncoming(_gamesMeId)
                ? $"Connect 4 invite from @{r.Requester.Handle}  ·  accept to play"
                : $"Connect 4 invite to @{r.Addressee.Handle}  ·  waiting to be accepted";
        }
        else if (it.Game is { } g)
        {
            var opp = g.Opponent(_gamesMeId);
            string state = g.MoveCount == 0 ? "new game" : "in play";
            string turn = g.IsTurnOf(_gamesMeId) ? "your turn" : $"waiting for @{opp?.Handle}";
            line = $"Connect 4 vs @{opp?.Handle ?? "?"}  ·  {state}  ·  {turn}";
        }
        else return;
        Tooltip().ShowLines([new(line, OverlayTooltip.FgColor, false)], ToScreen(it.Rect.Left, it.Rect.Bottom + 4));
    }
}

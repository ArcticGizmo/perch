using Avalonia;
using Avalonia.Media;
using Perch.Avalonia.Rendering;
using Perch.Data;

namespace Perch.Avalonia.Views;

/// <summary>
/// The usage strip's sign-in alerts, from <see cref="UsageInfo.Auth"/>. Nothing shows while both clocks rest; once
/// one crosses its threshold (<see cref="AuthExpiryRules"/>) a chip appears at the right of the account's heading (or
/// its collapsed tile's name row) — an hourglass with the time until the access token expires, a key with the time
/// until the login does (when the account must <c>/login</c> again). The chips wear the major-outage footer's red
/// (a tinted fill under red text), so they read as "act on this". The usage tooltip always lists both clocks.
/// The labels are cached strings (<see cref="AuthExpiryRules.Short"/>) and the glyph shapes are parsed once, so paint
/// formats and builds nothing.
/// </summary>
public sealed partial class OverlayCanvas
{
    private bool _showAuthExpiry = true;

    private const double AuthChipGap = 5;        // between two chips, and between the chips and the account name
    private const double AuthChipPadH = 5;       // chip inner horizontal padding
    private const double AuthChipInnerGap = 3;   // between a chip's glyph and its countdown
    private const double AuthChipFont = 9;
    private const double HourglassW = 8, HourglassH = 10;
    private const double KeyW = 12, KeyH = 8;

    // The major-outage footer's red, so a sign-in alert reads at the same severity as a service outage.
    private static Color AuthChipColor => FooterMajorColor;

    /// <summary>Show/hide the sign-in alerts on the account headings. They sit in the heading row's spare width, so
    /// no relayout.</summary>
    public void SetShowAuthExpiry(bool show)
    {
        if (_showAuthExpiry == show) return;
        _showAuthExpiry = show;
        if (UsageStripVisible) InvalidateVisual();
    }

    // Paints the chips for whichever clocks have crossed their threshold, right-aligned to `right` and centred on
    // midY — the login outermost, the access token inside it — and returns their left edge (`right` when none drew).
    private double DrawAuthChips(DrawingContext ctx, AuthExpiry auth, double right, double midY, DateTime now)
    {
        if (!_showAuthExpiry) return right;
        double x = right;
        if (auth.RefreshExpiresAt is { } login
            && AuthExpiryRules.Level(login, now, AuthExpiryRules.RefreshSoon) != AuthExpiryLevel.Normal)
            x = DrawAuthChip(ctx, isLogin: true, AuthExpiryRules.Short(login - now), x, right, midY);
        if (auth.AccessExpiresAt is { } access
            && AuthExpiryRules.Level(access, now, AuthExpiryRules.AccountAccessSoon) != AuthExpiryLevel.Normal)
            x = DrawAuthChip(ctx, isLogin: false, AuthExpiryRules.Short(access - now), x, right, midY);
        return x;
    }

    // One chip ending at x (less the gap after a previous chip): a tinted rounded fill sized from the label's line
    // height, the glyph, then the countdown. Returns its left edge.
    private double DrawAuthChip(DrawingContext ctx, bool isLogin, string label, double x, double right, double midY)
    {
        if (x < right) x -= AuthChipGap;
        var brush = OverlayDraw.Brush(AuthChipColor);
        var ft = OverlayDraw.Text(label, AuthChipFont, brush, FontWeight.SemiBold);
        double glyphW = isLogin ? KeyW : HourglassW;
        double w = AuthChipPadH + glyphW + AuthChipInnerGap + ft.Width + AuthChipPadH;
        double h = Math.Max(ft.Height, HourglassH) + 2;
        var chip = new Rect(x - w, midY - h / 2, w, h);
        OverlayDraw.Panel(ctx, chip, OverlayDraw.Brush(AuthChipColor, 0.18), null, 4);

        double gx = chip.X + AuthChipPadH;
        if (isLogin) DrawAuthGlyph(ctx, KeyGlyph.Value, brush, gx, midY - KeyH / 2);
        else DrawAuthGlyph(ctx, HourglassGlyph.Value, brush, gx, midY - HourglassH / 2);
        OverlayDraw.TextLeftMid(ctx, ft, gx + glyphW + AuthChipInnerGap, midY);
        return chip.X;
    }

    // The glyph shapes, parsed once (lazily — geometry needs the render platform up) in their own small boxes and
    // translated into place, so a paint allocates no geometry.
    private sealed record AuthGlyphShape(Geometry Fill, Geometry? Stroke);

    // An hourglass, HourglassW × HourglassH: solid caps top and bottom, a curved glass between them, and sand —
    // a little left in the top bulb, a mound in the bottom one.
    private static readonly Lazy<AuthGlyphShape> HourglassGlyph = new(() => new AuthGlyphShape(
        Geometry.Parse("M0,0 H8 V1.4 H0 Z M0,8.6 H8 V10 H0 Z "
                     + "M2.3,2.7 H5.7 C5.5,3.5 4.5,3.9 4,4.4 C3.5,3.9 2.5,3.5 2.3,2.7 Z "
                     + "M1.9,8.6 C1.9,7.5 3.3,6.9 4,6.4 C4.7,6.9 6.1,7.5 6.1,8.6 Z"),
        Geometry.Parse("M1.2,1.4 C1.2,3.7 3.3,4.2 3.3,5 C3.3,5.8 1.2,6.3 1.2,8.6 "
                     + "M6.8,1.4 C6.8,3.7 4.7,4.2 4.7,5 C4.7,5.8 6.8,6.3 6.8,8.6")));

    // A key, KeyW × KeyH: a round bow with a hole, the blade running right, two teeth hanging off it. One nonzero
    // path: the hole is wound against the bow, so it punches through while the blade's overlap with the bow fills.
    private static readonly Lazy<AuthGlyphShape> KeyGlyph = new(() => new AuthGlyphShape(
        Geometry.Parse("F1 M0,4 A3.2,3.2 0 1 1 6.4,4 A3.2,3.2 0 1 1 0,4 Z "
                     + "M1.75,4 A1.45,1.45 0 1 0 4.65,4 A1.45,1.45 0 1 0 1.75,4 Z "
                     + "M5.6,3.3 H12 V4.7 H5.6 Z M8.9,4.7 H10.2 V7 H8.9 Z M10.8,4.7 H12 V6.3 H10.8 Z"),
        null));

    private static void DrawAuthGlyph(DrawingContext ctx, AuthGlyphShape shape, IBrush brush, double x, double y)
    {
        using (ctx.PushTransform(Matrix.CreateTranslation(x, y)))
        {
            ctx.DrawGeometry(brush, null, shape.Fill);
            if (shape.Stroke is { } s) ctx.DrawGeometry(null, OverlayDraw.Pen(brush, 1.0), s);
        }
    }

    // True when the tooltip's sign-in lines will show a lapsed access token — the reason a stale reading can't refresh
    // (the poll authenticates with that token), so the usage section's own stale reason would only repeat it.
    private bool AuthLinesExplainStale(AuthExpiry auth, DateTime now) =>
        _showAuthExpiry && auth.AccessExpiresAt is { } a && a <= now;

    // The usage tooltip's sign-in lines for one account, under a divider: each clock in words, red once it has
    // crossed its threshold, with what to do once it matters.
    private void AddAuthTooltipLines(List<OverlayTooltip.Line> lines, AuthExpiry auth, DateTime now)
    {
        if (!_showAuthExpiry || !auth.IsKnown) return;
        lines.Add(OverlayTooltip.Line.Divider);

        if (auth.AccessExpiresAt is { } a)
        {
            var level = AuthExpiryRules.Level(a, now, AuthExpiryRules.AccountAccessSoon);
            string text = level == AuthExpiryLevel.Expired
                ? $"Access token  expired {AuthExpiryRules.Long(now - a)} ago — usage paused until Claude Code runs"
                : $"Access token  expires in {AuthExpiryRules.Long(a - now)} ({a:t})";
            lines.Add(new(text, level == AuthExpiryLevel.Normal ? OverlayTooltip.FgColor : AuthChipColor, false));
        }
        if (auth.RefreshExpiresAt is { } r)
        {
            var level = AuthExpiryRules.Level(r, now, AuthExpiryRules.RefreshSoon);
            string text = level == AuthExpiryLevel.Expired
                ? "Login  expired — run /login in Claude Code for this account"
                : $"Login  expires in {AuthExpiryRules.Long(r - now)} ({r:ddd d MMM} {r:t})";
            lines.Add(new(text, level == AuthExpiryLevel.Normal ? OverlayTooltip.FgColor : AuthChipColor, false));
            if (level == AuthExpiryLevel.Soon)
                lines.Add(new("Run /login in Claude Code before then to avoid interruption", OverlayTooltip.MutedColor, false));
        }
    }
}

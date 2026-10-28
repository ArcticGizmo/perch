using Avalonia;
using Avalonia.Media;
using Perch.Avalonia.Rendering;
using Perch.Avalonia.Theming;
using Perch.Data;

namespace Perch.Avalonia.Views;

/// <summary>
/// The usage strip's "Login required" button. When an account can only be fixed by logging in again — its login
/// clock ran out, or the sign-in refresher had its refresh token rejected — its bars are washed out and a button sits
/// centred over them (over the mini bars on a collapsed chip). Clicking it raises <see cref="LoginRequested"/>, which
/// opens a terminal running <c>claude auth login</c> under that account's config dir. Perch never logs in itself.
/// The same spot carries the refresher's non-clickable "Renewing sign-in…" / "Sign-in renewed" pill.
/// </summary>
public sealed partial class OverlayCanvas
{
    // Accounts the refresher found dead (by real root, with why), pushed from the App's SignInRefreshHost.
    private IReadOnlyDictionary<string, string> _refresherLoginRequired = new Dictionary<string, string>();
    // The buttons painted this frame, with the dir each logs in (a merged dir's, when that's the one that needs it), for
    // hit-testing; rebuilt each paint like the other usage rects.
    private readonly List<(Rect Rect, OrgUsage Set, ClaudeConfigDir Dir)> _loginButtonRects = new();
    private OrgUsage? _hoveredLoginButton;

    private const double LoginButtonFont = 10.5, LoginButtonChipFont = 9;
    private const double LoginButtonPadH = 10, LoginButtonChipPadH = 5;
    // A chip's bars are only ~9px tall, so its button keeps a slim vertical pad to stay clear of the name row above.
    private const double LoginButtonPadV = 3, LoginButtonChipPadV = 1;

    // What the sign-in refresher is doing per account (by real root): renewing, or just renewed.
    private IReadOnlyDictionary<string, SignInActivity> _signInActivity = new Dictionary<string, SignInActivity>();

    /// <summary>The sign-in refresher's per-account activity, by real root. A pill over bars already laid out, so this
    /// only repaints.</summary>
    internal void SetSignInActivity(IReadOnlyDictionary<string, SignInActivity> byRoot)
    {
        _signInActivity = byRoot;
        if (UsageStripVisible) InvalidateVisual();
    }

    // Draws over `bars` whichever sign-in state applies to the account, from any of its dirs (several config dirs signed
    // into one account share these bars): the Login required button wins, else the refresher's renewing / renewed pill.
    // Nothing when neither applies.
    private void DrawSignInOverlay(DrawingContext ctx, Rect bars, OrgUsage set, DateTime now, bool compact)
    {
        if (LoginNeedOf(set, now) is { } need)
            DrawLoginRequired(ctx, bars, set, need.Dir, compact);
        else if (set.Activity(_signInActivity) is { } activity)
            DrawSignInActivity(ctx, bars, activity, compact);
    }

    // A non-interactive status pill centred over the bars, on a lighter wash than the button's: the accent while the CLI
    // runs, the running green once renewed. A tinted fill under coloured text, like the sign-in chips.
    private void DrawSignInActivity(DrawingContext ctx, Rect bars, SignInActivity activity, bool compact)
    {
        OverlayDraw.Panel(ctx, bars.Inflate(new Thickness(2, 1)), OverlayDraw.Brush(BgColor, 0.55), null, 4);

        var color = activity == SignInActivity.Renewing ? Palette.Accent : Palette.Green;
        double font = compact ? LoginButtonChipFont : LoginButtonFont;
        double padH = compact ? LoginButtonChipPadH : LoginButtonPadH;
        var fg = OverlayDraw.Brush(color);
        var (full, brief) = activity == SignInActivity.Renewing
            ? ("Renewing sign-in…", "Renewing…")
            : ("Sign-in renewed", "Renewed");
        var ft = OverlayDraw.Text(full, font, fg, FontWeight.SemiBold);
        if (ft.Width + 2 * padH > bars.Width)
            ft = OverlayDraw.Text(brief, font, fg, FontWeight.SemiBold);

        double w = Math.Min(bars.Width, ft.Width + 2 * padH);
        double h = ft.Height + 2 * (compact ? LoginButtonChipPadV : LoginButtonPadV);
        var pill = new Rect(bars.Center.X - w / 2, bars.Center.Y - h / 2, w, h);
        OverlayDraw.Panel(ctx, pill, OverlayDraw.Brush(BgColor), null, h / 2);
        OverlayDraw.Panel(ctx, pill, OverlayDraw.Brush(color, 0.22), null, h / 2);
        OverlayDraw.TextLeftMid(ctx, ft, pill.Center.X - ft.Width / 2, pill.Center.Y);
    }

    /// <summary>Raised when a "Login required" button is clicked, with the account's config dir.</summary>
    internal event Action<ClaudeConfigDir>? LoginRequested;

    /// <summary>The accounts the sign-in refresher reports need a login, by real root, with the reason. The button
    /// sits over bars already laid out, so this only repaints.</summary>
    internal void SetLoginRequired(IReadOnlyDictionary<string, string> byRoot)
    {
        _refresherLoginRequired = byRoot;
        if (UsageStripVisible) InvalidateVisual();
    }

    // Which of this account's dirs needs a login, and why, or null when none does.
    private LoginNeed? LoginNeedOf(OrgUsage set, DateTime now) => set.LoginNeed(_refresherLoginRequired, now);

    // Washes `bars` out and centres the button over it; a click logs in `dir`. `compact` is the collapsed chip's smaller
    // button, which falls back to "Log in" when the chip is too narrow for the full label.
    private void DrawLoginRequired(DrawingContext ctx, Rect bars, OrgUsage set, ClaudeConfigDir dir, bool compact)
    {
        OverlayDraw.Panel(ctx, bars.Inflate(new Thickness(2, 1)), OverlayDraw.Brush(BgColor, 0.72), null, 4);

        double font = compact ? LoginButtonChipFont : LoginButtonFont;
        double padH = compact ? LoginButtonChipPadH : LoginButtonPadH;
        var fg = Palette.OnAccentBrush;
        var ft = OverlayDraw.Text("Login required", font, fg, FontWeight.SemiBold);
        if (ft.Width + 2 * padH > bars.Width)
            ft = OverlayDraw.Text("Log in", font, fg, FontWeight.SemiBold);

        double w = Math.Min(bars.Width, ft.Width + 2 * padH);
        double h = ft.Height + 2 * (compact ? LoginButtonChipPadV : LoginButtonPadV);
        var button = new Rect(bars.Center.X - w / 2, bars.Center.Y - h / 2, w, h);
        bool hovered = ReferenceEquals(_hoveredLoginButton, set);
        OverlayDraw.Panel(ctx, button, hovered ? OverlayDraw.Brush(Palette.AccentHover) : Palette.AccentBrush, null, h / 2);
        OverlayDraw.TextLeftMid(ctx, ft, button.Center.X - ft.Width / 2, button.Center.Y);
        _loginButtonRects.Add((button, set, dir));
    }

    // The "Login required" button the point is over (its account, and the dir it logs in), or null.
    private (OrgUsage Set, ClaudeConfigDir Dir)? LoginButtonAt(Point p)
    {
        foreach (var (rect, set, dir) in _loginButtonRects)
            if (rect.Contains(p))
                return (set, dir);
        return null;
    }

    // Hover wash + hand cursor for the button; true while the pointer is over one.
    private bool UpdateLoginButtonHover(Point p)
    {
        var over = InUsageStrip(p) ? LoginButtonAt(p)?.Set : null;
        if (!ReferenceEquals(over, _hoveredLoginButton)) { _hoveredLoginButton = over; InvalidateVisual(); }
        return over is not null;
    }

    private bool ClearLoginButtonHover()
    {
        if (_hoveredLoginButton is null) return false;
        _hoveredLoginButton = null;
        return true;
    }

    private bool RouteLoginButtonClick(Point p)
    {
        if (!InUsageStrip(p) || LoginButtonAt(p) is not { } hit) return false;
        LoginRequested?.Invoke(hit.Dir);
        return true;
    }

    // The usage tooltip's lines for an account that needs a login: what happened, and what the button does.
    private void AddLoginRequiredTooltipLines(List<OverlayTooltip.Line> lines, OrgUsage set, DateTime now)
    {
        if (LoginNeedOf(set, now) is not { } need) return;
        lines.Add(OverlayTooltip.Line.Divider);
        lines.Add(new("Login required", AuthChipColor, true));
        lines.Add(new(need.Reason, OverlayTooltip.FgColor, false));
        lines.Add(new("Click Login required to run claude auth login for this account in a terminal",
            OverlayTooltip.MutedColor, false));
    }
}

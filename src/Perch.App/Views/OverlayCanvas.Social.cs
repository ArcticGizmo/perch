using Avalonia;
using Avalonia.Media;
using Perch.Avalonia.Rendering;
using Perch.Avalonia.Theming;

namespace Perch.Avalonia.Views;

/// <summary>
/// The overlay's Social sign-in strip: a slim clickable band at the bottom of the floating panel that gives
/// the overlay the same entry points as the Settings → Social page, so Social isn't Settings-only. It shows
/// when Social is enabled and you either aren't signed in <em>or</em> haven't claimed a handle yet:
/// <list type="bullet">
///   <item>signed out → "Sign in to Social" (click raises <see cref="SignInRequested"/>);</item>
///   <item>signed in, no handle → "Finish setup — claim a handle" (click raises <see cref="SocialManageRequested"/>,
///     which opens the Settings Social page where the handle is entered).</item>
/// </list>
/// Once you're signed in with a handle it takes no height (the feed strip stands in its place). Sign-out
/// lives on the overlay's right-click menu (see <c>ShowContextMenuAt</c>). Same owner-drawn / captured-hit-rect
/// discipline as the mic and media strips.
/// </summary>
public sealed partial class OverlayCanvas
{
    private const double SocialStripHeight = 30;
    private const double SocialTextSize    = 10.5;

    private static readonly IBrush SocialHoverBrush = new SolidColorBrush(Color.FromArgb(26, 255, 255, 255));

    private bool _socialEnabled;
    private bool _socialSignedIn;
    private bool _socialHasHandle;
    private bool _hoveredSocial;
    private Rect _socialSignInRect;

    /// <summary>Raised when the strip is clicked while signed out — the App starts GitHub sign-in.</summary>
    public event Action? SignInRequested;

    /// <summary>Raised to sign out (from the overlay's right-click menu).</summary>
    public event Action? SignOutRequested;

    /// <summary>Raised to open the Settings Social page (e.g. to claim a handle after signing in).</summary>
    public event Action? SocialManageRequested;

    // Whether Social is on at all (drives whether the menu offers sign-in/out and whether the strip can show).
    private bool SocialEnabled => _socialEnabled;
    private bool SocialSignedIn => _socialSignedIn;

    // Shown while Social is on and setup isn't finished (signed out, or signed in without a handle yet).
    private bool SocialSignInStripVisible => _socialEnabled && !(_socialSignedIn && _socialHasHandle);

    /// <summary>Enables/disables the whole Social feature on the overlay. Driven by <c>OverlaySettingsGates</c>
    /// from <c>AppSettings.SocialEnabled</c>.</summary>
    public void SetSocialEnabled(bool enabled)
    {
        if (_socialEnabled == enabled) return;
        bool before = SocialSignInStripVisible;
        _socialEnabled = enabled;
        if (SocialSignInStripVisible != before) RemeasurePanel();
    }

    /// <summary>Pushes the live auth state (on the UI thread): whether you're signed in, and whether you've
    /// claimed a handle. Signing in with a handle hides the strip; signing out (or having no handle) shows it.</summary>
    public void SetSocialAccount(bool signedIn, bool hasHandle)
    {
        if (_socialSignedIn == signedIn && _socialHasHandle == hasHandle) return;
        bool before = SocialSignInStripVisible;
        _socialSignedIn = signedIn;
        _socialHasHandle = hasHandle;
        if (SocialSignInStripVisible != before) RemeasurePanel(); else InvalidateVisual();
    }

    // Routed from RouteClick: signed out → sign in; signed in (but unfinished) → open Settings to claim.
    private void OnSocialStripClicked()
    {
        if (_socialSignedIn) SocialManageRequested?.Invoke();
        else SignInRequested?.Invoke();
    }

    private void ClearSocialHitRect() => _socialSignInRect = default;

    // Paints the strip at y=top (height already reserved in Draw): a separator, a small accent dot and the
    // state-appropriate prompt, with the whole band captured as one hit-rect.
    private void DrawSocialSignInStrip(DrawingContext ctx, double width, double top)
    {
        ClearSocialHitRect();
        ctx.DrawLine(SepPen, new Point(HorizPad, top + 0.5), new Point(width - HorizPad, top + 0.5));

        if (_hoveredSocial)
            OverlayDraw.Panel(ctx, new Rect(HorizPad - 4, top + 3, width - 2 * (HorizPad - 4), SocialStripHeight - 6),
                SocialHoverBrush, null, 6);

        double midY = top + SocialStripHeight / 2;
        ctx.DrawEllipse(Palette.AccentBrush, null, new Point(HorizPad + 3, midY), 3, 3);
        double x = HorizPad + 3 * 2 + 6;
        string text = _socialSignedIn ? "Finish setup — claim a handle" : "Sign in to Social";
        OverlayDraw.TextLeftMid(ctx, OverlayDraw.Text(text, SocialTextSize, Palette.AccentBrush, FontWeight.SemiBold), x, midY);

        _socialSignInRect = new Rect(0, top, width, SocialStripHeight);
    }
}

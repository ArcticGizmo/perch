using Avalonia;
using Avalonia.Media;
using Perch.Avalonia.Rendering;
using Perch.Avalonia.Theming;

namespace Perch.Avalonia.Views;

/// <summary>
/// The overlay's Social sign-in strip: a slim clickable band at the bottom of the floating panel, shown only
/// when Social is enabled but you're <em>signed out</em> (or a session couldn't be restored), so signing in
/// is one click from the overlay instead of a trip into Settings. When signed in it takes no height — the
/// feed strip stands in its place. Same owner-drawn / captured-hit-rect discipline as the mic and media
/// strips; the click raises <see cref="SignInRequested"/> for the App to drive the OAuth flow.
/// </summary>
public sealed partial class OverlayCanvas
{
    private const double SocialStripHeight = 30;
    private const double SocialTextSize    = 10.5;

    private static readonly IBrush SocialHoverBrush = new SolidColorBrush(Color.FromArgb(26, 255, 255, 255));

    private bool _socialEnabled;
    private bool _socialSignedIn;
    private bool _hoveredSocial;
    private Rect _socialSignInRect;

    /// <summary>Raised when the sign-in strip is clicked; the App starts the GitHub sign-in flow.</summary>
    public event Action? SignInRequested;

    // Shown only while Social is on and the user is signed out — the "please sign in" affordance.
    private bool SocialSignInStripVisible => _socialEnabled && !_socialSignedIn;

    /// <summary>Enables/disables the whole Social feature on the overlay (drives whether the sign-in strip can
    /// appear). Driven by <c>OverlaySettingsGates</c> from <c>AppSettings.SocialEnabled</c>.</summary>
    public void SetSocialEnabled(bool enabled)
    {
        if (_socialEnabled == enabled) return;
        bool before = SocialSignInStripVisible;
        _socialEnabled = enabled;
        if (SocialSignInStripVisible != before) RemeasurePanel();
    }

    /// <summary>Pushes the live auth state (on the UI thread). Signing in hides the sign-in strip; signing out
    /// brings it back. Changes the panel height, so relayout when the strip's visibility flips.</summary>
    public void SetSocialSignedIn(bool signedIn)
    {
        if (_socialSignedIn == signedIn) return;
        bool before = SocialSignInStripVisible;
        _socialSignedIn = signedIn;
        if (SocialSignInStripVisible != before) RemeasurePanel(); else InvalidateVisual();
    }

    private void ClearSocialHitRect() => _socialSignInRect = default;

    // Paints the strip at y=top (height already reserved in Draw): a separator, a small accent dot and the
    // "Sign in to Social" prompt, with the whole band captured as one hit-rect.
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
        OverlayDraw.TextLeftMid(ctx, OverlayDraw.Text("Sign in to Social", SocialTextSize, Palette.AccentBrush, FontWeight.SemiBold),
            x, midY);

        _socialSignInRect = new Rect(0, top, width, SocialStripHeight);
    }
}

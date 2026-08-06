using Avalonia;
using Avalonia.Media;
using Perch.Avalonia.Rendering;
using Perch.Data;
using Perch.Platform;

namespace Perch.Avalonia.Views;

/// <summary>
/// The overlay's microphone strip: a slim owner-drawn band naming whatever currently holds the microphone, whose
/// name is a link to that app's window. Opt-in, and only present while the mic is actually in use — otherwise it
/// takes no height.
///
/// <para><b>It answers one question: "what has my mic, and where is it?"</b> Deliberately not "am I muted".
/// Perch tried that, through Teams' local API, and the honest conclusion was that it can't be answered reliably:
/// the protocol has no way to ask for the current state, volunteers it only when it changes, and needs an in-app
/// pairing dance before it will talk at all — so a strip claiming to know your mute was wrong often enough to be
/// worse than one that never claimed it. The capture device's own mute isn't an answer either: the app in the
/// call doesn't know about it, so muting there produces exactly the "you're on mute" trap the feature was meant
/// to avoid. What's left is what the OS reports plainly and Perch can always stand behind — who holds the
/// device — plus the genuinely useful action of jumping to it. See <c>docs/mic-presence-investigation.md</c> for
/// the protocol findings, kept in case the ground shifts.</para>
///
/// Follows the same measure-or-paint / captured-hit-rect discipline as the rest of <see cref="OverlayCanvas"/>.
/// </summary>
public sealed partial class OverlayCanvas
{
    private const double MicStripHeight = 36;
    private const double MicTextSize    = 10.5;

    // A live mic is red — the same cue the Windows privacy indicator uses, and the only state the strip reports.
    private static readonly IBrush MicLiveBrush = new SolidColorBrush(Color.FromRgb(226, 106, 106));

    // The app name brightens on hover to signal that it's the jump-to-app control — the same "this text is a
    // link" cue the session rows use, rather than a separate button.
    private static readonly IBrush MicLinkHoverBrush = new SolidColorBrush(Color.FromRgb(255, 255, 255));

    private bool _micEnabled;
    private MicSnapshot? _mic;

    // The label is the strip's only control. _micLabelJumpable records whether its click can actually go
    // anywhere, so the name only brightens and takes the hand cursor when it can.
    private bool _hoveredMicLabel;
    private bool _micLabelJumpable;
    private Rect _micLabelRect;

    /// <summary>Raised when the user clicks the app's name; the App focuses the app holding the mic.</summary>
    public event Action? MicJumpRequested;

    // Visible only while something actually holds the mic. No link state to keep it up, and nothing to show
    // when the microphone is idle.
    private bool MicStripVisible => _micEnabled && _mic?.InUse == true;

    /// <summary>Show/hide the whole microphone strip. Toggling it can change the panel height (when the mic
    /// is in use), so relayout in that case; otherwise nothing visible changes.</summary>
    public void SetShowMicPresence(bool enabled)
    {
        if (_micEnabled == enabled) return;
        bool before = MicStripVisible;
        _micEnabled = enabled;
        if (MicStripVisible != before) RemeasurePanel();
    }

    /// <summary>Feeds the latest microphone snapshot (on the UI thread), or null when the platform can't
    /// report. The mic holder also drives whether the media strip suppresses a call app that grabbed the
    /// media controls, so a change here can flip <em>either</em> strip's visibility — relayout when it does,
    /// otherwise just repaint.</summary>
    public void UpdateMic(MicSnapshot? mic)
    {
        bool micBefore = MicStripVisible;
        bool mediaBefore = MediaStripVisible;
        _mic = mic;
        if (MicStripVisible != micBefore || MediaStripVisible != mediaBefore) RemeasurePanel();
        else if (_micEnabled) InvalidateVisual();
    }

    private void ClearMicHitRects()
    {
        _micLabelRect = default;
        _micLabelJumpable = false;
    }

    /// <summary>Whether the point is over the clickable app name — the jump target.</summary>
    private bool HitTestMicLabel(Point p) => _micLabelJumpable && _micLabelRect.Contains(p);

    // Paints the strip at y=top (its height is already reserved in Draw): a separator, the mic glyph, and the
    // app's name, which is itself the jump-to-app control.
    private void DrawMicStrip(DrawingContext ctx, double width, double top)
    {
        ClearMicHitRects();
        if (!MicStripVisible) return;

        ctx.DrawLine(SepPen, new Point(HorizPad, top + 0.5), new Point(width - HorizPad, top + 0.5));

        double midY = top + MicStripHeight / 2;
        DrawMicGlyph(ctx, MicLiveBrush, HorizPad + 3, midY);

        double textX = HorizPad + 13;
        double textMax = width - HorizPad - textX;
        if (textMax <= 8) return;

        // Only clickable when we know a process to jump to: the privacy ledger can name an app without
        // attributing a live pid to it, and a name that highlights but goes nowhere is worse than a plain one.
        _micLabelJumpable = (_mic?.Primary?.ProcessId ?? 0) > 0;

        string name = _mic?.Primary?.DisplayName ?? "Microphone";
        var brush = _micLabelJumpable && _hoveredMicLabel ? MicLinkHoverBrush : FgBrush;
        OverlayDraw.TextLeftMid(ctx, OverlayDraw.Text(OverlayDraw.Truncate(name, MicTextSize, textMax),
            MicTextSize, brush), textX, midY);

        // Always a dwell target (unlike the media strip's label, which only tooltips when truncated): the
        // tooltip carries the device, the elapsed time and any other apps, none of which fit on the strip.
        _micLabelRect = new Rect(HorizPad, top, width - HorizPad - HorizPad, MicStripHeight);
    }

    // A small microphone: a rounded capsule head, a cradle, and a short stem.
    private static void DrawMicGlyph(DrawingContext ctx, IBrush b, double x, double cy)
    {
        const double w = 5.0, h = 7.5;
        double cx = x + w / 2;
        OverlayDraw.Panel(ctx, new Rect(cx - w / 2, cy - h / 2 - 2, w, h), b, null, w / 2);

        var pen = new Pen(b, 1.3);
        ctx.DrawLine(pen, new Point(cx - 3.6, cy + 2.2), new Point(cx + 3.6, cy + 2.2));
        ctx.DrawLine(pen, new Point(cx, cy + 2.2), new Point(cx, cy + 5.4));
    }

    // The dwell tooltip: the app, which device it's on, how long it's held it, and anything else holding it.
    private void ShowMicTooltip()
    {
        if (!MicStripVisible || _micLabelRect.Width <= 0) return;

        var lines = new List<OverlayTooltip.Line>();
        var primary = _mic?.Primary;

        lines.Add(new(primary?.DisplayName ?? "Microphone", OverlayTooltip.FgColor, true));

        if (primary is { IsStreaming: false })
            lines.Add(new("Holding the mic, not streaming", OverlayTooltip.MutedColor, false));

        if (primary?.Since is { } since)
            lines.Add(new($"Since {since.ToLocalTime():HH:mm} ({FormatMicElapsed(DateTimeOffset.Now - since)})",
                OverlayTooltip.MutedColor, false));

        if (_mic?.DeviceName is { Length: > 0 } device)
            lines.Add(new(device, OverlayTooltip.MutedColor, false));

        // Every other app holding the mic at the same time — a call plus a recorder is a normal setup, and
        // hiding the second one would make the strip look wrong rather than incomplete.
        if (_mic is { Users.Count: > 1 })
            lines.Add(new("Also: " + string.Join(", ", _mic.Users.Skip(1).Select(u => u.DisplayName)),
                OverlayTooltip.MutedColor, false));

        if (_micLabelJumpable)
            lines.Add(new("Click the name to jump to it", OverlayTooltip.MutedColor, false));

        Tooltip().ShowLines(lines, ToScreen(_micLabelRect.Left, _micLabelRect.Bottom + 4));
    }

    private static string FormatMicElapsed(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        if (t.TotalMinutes < 1) return $"{(int)t.TotalSeconds}s";
        if (t.TotalHours < 1) return $"{(int)t.TotalMinutes}m";
        return $"{(int)t.TotalHours}h {t.Minutes:00}m";
    }
}

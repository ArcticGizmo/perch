using Avalonia;
using Avalonia.Media;
using Perch.Avalonia.Rendering;
using Perch.Data;
using Perch.Platform;

namespace Perch.Avalonia.Views;

/// <summary>
/// The overlay's microphone strip: a slim owner-drawn band naming whatever currently holds the microphone,
/// with a button to jump to that app's window and a button to mute. Opt-in, and only present while the mic
/// is actually in use — otherwise it takes no height.
///
/// <para><b>Generic first.</b> Everything the strip draws comes from the app-agnostic
/// <see cref="MicSnapshot"/>, so a Zoom call, a Slack huddle, a browser tab or OBS all get named, jumped to
/// and device-muted identically. The <see cref="CallSnapshot"/> half is layered on top <em>only</em> when a
/// recognised app (currently Teams — see <see cref="MicApps.Classify"/>) has a live control channel; then
/// the strip upgrades to real meeting state and in-app mute. Nothing below branches on a product name: the
/// question it asks is "do I have a call link for whatever is holding the mic", and the answer changes the
/// wording and which mute is used, not the layout.</para>
///
/// Follows the same measure-or-paint / captured-hit-rect discipline as the rest of
/// <see cref="OverlayCanvas"/>, and deliberately reuses the now-playing strip's button brushes so the two
/// bands read as one family of controls.
/// </summary>
public sealed partial class OverlayCanvas
{
    private const double MicStripHeight = 36;
    private const double MicTextSize    = 10.5;

    // A hot mic is red; a muted one goes grey, so the strip's state is legible from the colour alone.
    private static readonly IBrush MicLiveBrush  = new SolidColorBrush(Color.FromRgb(226, 106, 106));
    private static readonly IBrush MicMutedBrush = new SolidColorBrush(Color.FromRgb(132, 132, 148));

    // The app name brightens on hover to signal that it's the jump-to-app control — the same "this text is a
    // link" cue the session rows use, rather than a separate button competing with the mute one.
    private static readonly IBrush MicLinkHoverBrush = new SolidColorBrush(Color.FromRgb(255, 255, 255));

    private bool _micEnabled;
    private MicSnapshot? _mic;
    private CallSnapshot? _call;
    private CallLinkState _callLink = CallLinkState.Disabled;

    // Hit-rects are captured at paint time; the mute button gets a zero-size rect when it can't act, so it
    // can't be hovered or clicked. The label is its own target — clicking the app's name jumps to it, which is
    // the obvious gesture and means the strip needs no separate jump button. _micLabelJumpable records
    // whether that click can actually go anywhere, so the name only takes the hand cursor when it can.
    private bool _hoveredMicMute;
    private bool _hoveredMicLabel;
    private bool _micLabelJumpable;
    private Rect _micMuteRect, _micLabelRect;

    /// <summary>Raised when the user clicks the app's name; the App focuses the app holding the mic.</summary>
    public event Action? MicJumpRequested;

    /// <summary>Raised when the user clicks the mute button. The App decides <em>which</em> mute that means —
    /// the call app's own when a link is live, otherwise the capture device's.</summary>
    public event Action? MicMuteToggleRequested;

    // Whether a recognised call app's control channel is live and says we're in a call. This is the only
    // gate on the product-specific behaviour: no link (or a link belonging to an app that isn't the one
    // holding the mic) means the strip stays on the generic path.
    private bool CallControlsAvailable => MicApps.CallLinkApplies(_mic, _call, _callLink);

    // Visible when something holds the mic, or when a call link reports a meeting even though the mic
    // monitor hasn't reported anything — which is exactly the mac head's position today (its monitor is a
    // stub, but the Teams link works), and also covers a call joined before the strip was switched on.
    private bool MicStripVisible => _micEnabled && (_mic?.InUse == true || CallControlsAvailable);

    // Muted state to draw: the app's own mute when we have a link (that's what other participants
    // experience), else the capture device's.
    private bool MicShowsMuted => CallControlsAvailable ? _call!.IsMuted : _mic?.DeviceMuted == true;

    // Whether the mute button can actually do anything. The device mute is always available, but a call app
    // can refuse the toggle — a meeting where the organiser has hard-muted everyone is the real case — and
    // there the honest answer is a disabled button, not one that silently no-ops. Falling back to the device
    // mute would be worse than useless: it can't unmute you in the meeting, only make you inaudible again
    // once the organiser lets you speak.
    private bool MicMuteAvailable => !CallControlsAvailable || _call!.CanToggleMute;

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
    /// report. Relayouts when the strip's visibility flips, otherwise just repaints.</summary>
    public void UpdateMic(MicSnapshot? mic)
    {
        bool before = MicStripVisible;
        _mic = mic;
        if (MicStripVisible != before) RemeasurePanel();
        else if (_micEnabled) InvalidateVisual();
    }

    /// <summary>Feeds the latest call-app state and link status (on the UI thread). Both move together, so
    /// they arrive together — the link state is what decides whether the snapshot may be trusted.</summary>
    public void UpdateCall(CallSnapshot? call, CallLinkState link)
    {
        bool before = MicStripVisible;
        _call = call;
        _callLink = link;
        if (MicStripVisible != before) RemeasurePanel();
        else if (_micEnabled) InvalidateVisual();
    }

    private void ClearMicHitRects()
    {
        _micMuteRect = _micLabelRect = default;
        _micLabelJumpable = false;
    }

    /// <summary>Whether the point is over the clickable app name — the jump target. Separate from the mute
    /// button so the App can offer the hand cursor without the button's hover wash following the pointer
    /// across the whole strip.</summary>
    private bool HitTestMicLabel(Point p) => _micLabelJumpable && _micLabelRect.Contains(p);

    private bool HitTestMicMute(Point p) => _micMuteRect.Contains(p);

    // Paints the strip at y=top (its height is already reserved in Draw): a separator, the mute button on the
    // right, and the mic glyph + "App — state" on the left, where the text itself is the jump-to-app control.
    private void DrawMicStrip(DrawingContext ctx, double width, double top)
    {
        ClearMicHitRects();
        if (!MicStripVisible) return;

        ctx.DrawLine(SepPen, new Point(HorizPad, top + 0.5), new Point(width - HorizPad, top + 0.5));

        double midY = top + MicStripHeight / 2;
        bool muted = MicShowsMuted;
        var accent = muted ? MicMutedBrush : MicLiveBrush;

        // Mute is the only button; jumping to the app is the label's job.
        const double Btn = 22;
        double muteCx = width - HorizPad - Btn / 2 + 2;
        _micMuteRect = DrawMicMuteButton(ctx, muteCx, midY, Btn, muted);
        double clusterLeft = muteCx - Btn / 2;

        // Left: the mic glyph, then the label truncated to whatever space is left before the button.
        double textX = HorizPad;
        DrawMicGlyph(ctx, accent, textX + 3, midY, muted);
        textX += 13;
        double textMax = clusterLeft - 8 - textX;
        if (textMax > 8)
        {
            // Only clickable when we know a process to jump to: the privacy ledger can name an app without
            // attributing a live pid to it, and a name that highlights but goes nowhere is worse than a plain one.
            _micLabelJumpable = (_mic?.Primary?.ProcessId ?? 0) > 0;

            string label = MicLabel();
            string shown = OverlayDraw.Truncate(label, MicTextSize, textMax);
            var brush = _micLabelJumpable && _hoveredMicLabel ? MicLinkHoverBrush : FgBrush;
            OverlayDraw.TextLeftMid(ctx, OverlayDraw.Text(shown, MicTextSize, brush), textX, midY);

            // The label is always a dwell target (unlike the media strip's, which only tooltips when
            // truncated): the tooltip carries the device name and the call-link hint, which are never on the
            // strip itself and are the things a user actually needs when something isn't working.
            _micLabelRect = new Rect(HorizPad, top, clusterLeft - 8 - HorizPad, MicStripHeight);
        }
    }

    // "Microsoft Teams — muted", "Slack — using your mic", … The state half comes from the call link when we
    // have one (it knows about a meeting), else from the mic itself.
    private string MicLabel()
    {
        string name = _mic?.Primary?.DisplayName
                      ?? (CallControlsAvailable ? "Microsoft Teams" : "Microphone");

        string state = CallControlsAvailable
            ? (_call!.IsMuted ? "muted" : "in a meeting")
            : _mic?.DeviceMuted == true ? "mic muted" : "using your mic";

        return $"{name}  —  {state}";
    }

    // The mute button. Greyed with a zero hit-rect when the call app won't accept a toggle, matching how the
    // media strip disables a transport button the source doesn't support.
    private Rect DrawMicMuteButton(DrawingContext ctx, double cx, double cy, double box, bool muted)
    {
        var rect = new Rect(cx - box / 2, cy - box / 2, box, box);
        bool enabled = MicMuteAvailable;
        bool hovered = enabled && _hoveredMicMute;
        if (hovered) OverlayDraw.Panel(ctx, rect, MediaHoverBrush, null, 5);

        var brush = !enabled ? MediaDisabledBrush
            : muted ? MicMutedBrush
            : hovered ? FgBrush
            : MediaBtnBrush;
        DrawMicGlyph(ctx, brush, cx - 4, cy, muted);
        return enabled ? rect : default;
    }

    // A small microphone: a rounded capsule head, a stand, and a strike-through when muted.
    private static void DrawMicGlyph(DrawingContext ctx, IBrush b, double x, double cy, bool muted)
    {
        const double w = 5.0, h = 7.5;
        double cx = x + w / 2;
        OverlayDraw.Panel(ctx, new Rect(cx - w / 2, cy - h / 2 - 2, w, h), b, null, w / 2);

        var pen = new Pen(b, 1.3);
        // The cradle under the head, plus the short stem to the base.
        ctx.DrawLine(pen, new Point(cx - 3.6, cy + 2.2), new Point(cx + 3.6, cy + 2.2));
        ctx.DrawLine(pen, new Point(cx, cy + 2.2), new Point(cx, cy + 5.4));

        if (muted)
            ctx.DrawLine(new Pen(b, 1.5), new Point(cx - 5.5, cy + 6), new Point(cx + 5.5, cy - 7));
    }

    // The dwell tooltip: the app, which device it's on, how long it's held it, and — when a recognised call
    // app's link isn't working — what the user can do about it. That last line is the whole reason the label
    // is always a dwell target: an integration that is silently absent is worse than one that explains itself.
    private void ShowMicTooltip()
    {
        if (!MicStripVisible || _micLabelRect.Width <= 0) return;

        var lines = new List<OverlayTooltip.Line>();
        var primary = _mic?.Primary;

        lines.Add(new(primary?.DisplayName ?? "Microphone", OverlayTooltip.FgColor, true));

        if (CallControlsAvailable)
        {
            var call = _call!;
            var bits = new List<string> { call.IsMuted ? "muted" : "unmuted" };
            if (call.IsCameraOn) bits.Add("camera on");
            if (call.IsSharing) bits.Add("sharing");
            if (call.IsHandRaised) bits.Add("hand raised");
            if (call.IsRecording) bits.Add("recording");
            lines.Add(new("In a meeting · " + string.Join(" · ", bits), OverlayTooltip.MutedColor, false));
        }
        else if (_mic?.DeviceMuted == true)
        {
            // Be explicit that this mute is the device's, not the app's: the app still thinks it's live, and
            // that misunderstanding is the classic "you're on mute" moment.
            lines.Add(new("Capture device muted — the app doesn't know", OverlayTooltip.MutedColor, false));
        }

        if (!MicMuteAvailable)
            lines.Add(new($"{primary?.DisplayName ?? "The call app"} won't accept a mute change right now",
                OverlayTooltip.MutedColor, false));

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

        if (CallLinkHint() is { } hint)
            lines.Add(new(hint, OverlayTooltip.MutedColor, false));

        Tooltip().ShowLines(lines, ToScreen(_micLabelRect.Left, _micLabelRect.Bottom + 4));
    }

    // What to say when a recognised call app is holding the mic but its control channel isn't usable. Only
    // ever shown for an app Perch actually has an integration for — there's nothing to suggest otherwise, and
    // nagging about a missing integration for Zoom or OBS would just be noise.
    private string? CallLinkHint()
    {
        if (CallControlsAvailable) return null;
        if (_mic?.Primary is not { } primary) return null;
        if (MicApps.Classify(primary.Identity) != MicAppKind.Teams) return null;

        return _callLink switch
        {
            CallLinkState.Disabled => "Mute inside Teams: enable Teams call controls in Perch settings",
            CallLinkState.AwaitingApproval => "Waiting for you to approve Perch in Teams",
            CallLinkState.Unavailable => "Teams controls unavailable — turn on Settings › Privacy › "
                                        + "Third-party app API in Teams",
            _ => null,
        };
    }

    private static string FormatMicElapsed(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        if (t.TotalMinutes < 1) return $"{(int)t.TotalSeconds}s";
        if (t.TotalHours < 1) return $"{(int)t.TotalMinutes}m";
        return $"{(int)t.TotalHours}h {t.Minutes:00}m";
    }
}

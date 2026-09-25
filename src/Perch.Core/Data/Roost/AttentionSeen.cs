namespace Perch.Data.Roost;

/// <summary>
/// Whether something that wants the user is already in front of them, so a desktop cue (toast, chime, push)
/// for it would only be noise (UI-free, unit-tested). Used for both the Perch session window's pending-prompt cue
/// (<c>SessionAttention</c>'s "window active" input) and the monitor's done / waiting / API-error toasts.
///
/// <para>Seen = the session's own window is the active one, <em>or</em> the Roost is the active window and the
/// session's pane is on screen — expanded <em>or</em> as a mini card, since a mini card carries the pending
/// "⚠ Allow …" / "✓ Done" line too (and a pending permission usually lands before the next scan has expanded
/// its pane). A Roost that's open but not active, or a pane scrolled/filtered/zoomed out of view, isn't seen.</para>
/// </summary>
public static class AttentionSeen
{
    public static bool Seen(bool ownWindowActive, bool roostActive, bool paneOnScreen) =>
        ownWindowActive || (roostActive && paneOnScreen);
}

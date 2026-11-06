namespace Perch.Platform;

/// <summary>
/// The outcome of <see cref="IWindowActivator.FocusTerminal"/>. <see cref="Raised"/> is the window-level
/// answer (see <see cref="IWindowActivator.FocusTerminalForProcess"/>); <see cref="TabMiss"/> is set when the
/// window was raised but its tabbed terminal could not be steered to the session's own tab, so the caller can
/// say why instead of leaving the user on some other tab without a hint.
/// </summary>
public readonly record struct TerminalFocus(bool Raised, TabMiss? TabMiss = null);

/// <param name="Reason">Why no tab was chosen.</param>
/// <param name="Title">The session's console title, i.e. the tab name Perch looked for.</param>
/// <param name="SameTitleCount">How many of the window's tabs carry <paramref name="Title"/> (Ambiguous only).</param>
public sealed record TabMiss(TabMissReason Reason, string Title, int SameTitleCount);

public enum TabMissReason
{
    /// <summary>Several tabs carry the session's title, and tagging it to tell them apart didn't show up.</summary>
    Ambiguous,

    /// <summary>No tab carries the session's title: renamed, set not to follow the app's title, or in a
    /// split pane that isn't the tab's focused one.</summary>
    NotFound,
}

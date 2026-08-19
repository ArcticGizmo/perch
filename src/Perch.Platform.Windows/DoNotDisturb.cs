using System.Runtime.InteropServices;
using Perch.Platform;

namespace Perch.Platform.Windows;

/// <summary>
/// Windows <see cref="IDoNotDisturb"/> via <c>SHQueryUserNotificationState</c> — the shell's own "should I show
/// notifications right now?" signal. We treat <c>QUNS_QUIET_TIME</c> (Do Not Disturb / Focus Assist quiet hours)
/// as DND; a fullscreen app or presentation mode is a different kind of "busy" and is deliberately not folded in
/// here. Best-effort: any failure reads as "not in DND", so the feature never wrongly hides things.
/// </summary>
public sealed class WindowsDoNotDisturb : IDoNotDisturb
{
    public bool IsActive =>
        SHQueryUserNotificationState(out var state) == 0 && state == QueryUserNotificationState.QUNS_QUIET_TIME;

    // The subset we care about; see the QUERY_USER_NOTIFICATION_STATE enum in shellapi.h.
    private enum QueryUserNotificationState
    {
        QUNS_NOT_PRESENT = 1,
        QUNS_BUSY = 2,
        QUNS_RUNNING_D3D_FULL_SCREEN = 3,
        QUNS_PRESENTATION_MODE = 4,
        QUNS_ACCEPTS_NOTIFICATIONS = 5,
        QUNS_QUIET_TIME = 6,
        QUNS_APP = 7,
    }

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out QueryUserNotificationState state);
}

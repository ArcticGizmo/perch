using System.Runtime.InteropServices;
using Microsoft.Win32;
using Perch.Platform;

namespace Perch.Platform.Windows;

/// <summary>
/// Windows <see cref="IDoNotDisturb"/>. Windows 11's <b>manual</b> "Do not disturb" toggle isn't reliably
/// reflected by <c>SHQueryUserNotificationState</c> (that API tracks the older scheduled Focus Assist / quiet
/// hours), so we read the Focus/quiet-hours state out of the CloudStore registry blob first and fall back to
/// the shell API for the scheduled case. Best-effort throughout: any failure reads as "not in DND", so the
/// feature never wrongly hides things.
/// </summary>
public sealed class WindowsDoNotDisturb : IDoNotDisturb
{
    public bool IsActive => QuietHoursOn() || ShellQuietTime();

    // Windows 11 stores the Focus / Do Not Disturb state in a CloudStore blob whose Data value embeds the
    // active focus profile as a UTF-16 string: "...QuietHoursProfile.Unrestricted" (off),
    // ".PriorityOnly" or ".Alarms" (a Focus / Do Not Disturb mode is on). We read the profile name rather than
    // a fixed byte offset — the binary layout is version-sensitive (an earlier byte-8 heuristic misfired,
    // reading a timestamp byte as the mode), but the profile string is stable. This is what actually flips
    // when you toggle DND in the Action Center.
    private static bool QuietHoursOn()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\CloudStore\Store\Cache\DefaultAccount\" +
                @"$$windows.data.notifications.quiethourssettings\Current");
            if (key?.GetValue("Data") is byte[] data && data.Length > 0)
            {
                var s = System.Text.Encoding.Unicode.GetString(data);
                if (s.Contains("Unrestricted", StringComparison.Ordinal)) return false;   // notifications allowed = off
                if (s.Contains("PriorityOnly", StringComparison.Ordinal)
                    || s.Contains("Alarms", StringComparison.Ordinal)) return true;       // a Focus/DND mode is on
            }
        }
        catch { /* best-effort */ }
        return false;   // unknown / unreadable → treat as off, so nothing is wrongly hidden
    }

    private static bool ShellQuietTime()
    {
        try
        {
            return SHQueryUserNotificationState(out var state) == 0
                   && state == QueryUserNotificationState.QUNS_QUIET_TIME;
        }
        catch { return false; }
    }

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

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

    // Toggling has no documented API: FocusSessionManager.TryStartFocusSession is a Limited Access Feature
    // (needs a Microsoft-issued unlock token), rewriting the CloudStore blob doesn't take effect live, and
    // publishing the shell's WNF quiet-hours state is ignored on 26200. What does it is the shell's own
    // QuietHoursSettings out-of-process COM server: its UserSelectedProfile setter is what Settings and the
    // Action Center bell go through. The interface is undocumented (reverse-engineered by Rafael Rivera:
    // gist.github.com/riverar/085d98ffb1343e92225a10817109b2e3); only its first two slots, the
    // UserSelectedProfile get/put pair, are declared here, which is also the part shipping apps (Telegram) use.
    // It may break on a future build; it fails soft (false) rather than throwing. Blocking cross-process call,
    // so callers keep it off the UI thread.
    public bool CanToggle => OperatingSystem.IsWindows();

    private const string ProfileUnrestricted = "Microsoft.QuietHoursProfile.Unrestricted";
    private const string ProfilePriorityOnly = "Microsoft.QuietHoursProfile.PriorityOnly";
    private static readonly Guid CLSID_QuietHoursSettings = new("f53321fa-34f8-4b7f-b9a3-361877cb94cf");

    public bool TrySetActive(bool active)
    {
        IQuietHoursSettings? settings = null;
        try
        {
            var type = Type.GetTypeFromCLSID(CLSID_QuietHoursSettings);
            if (type is null) return false;
            settings = Activator.CreateInstance(type) as IQuietHoursSettings;
            if (settings is null) return false;
            return settings.SetUserSelectedProfile(active ? ProfilePriorityOnly : ProfileUnrestricted) == 0;
        }
        catch { return false; }
        finally
        {
            if (settings is not null) Marshal.FinalReleaseComObject(settings);
        }
    }

    [ComImport]
    [Guid("6bff4732-81ec-4ffb-ae67-b6c1bc29631f")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IQuietHoursSettings
    {
        [PreserveSig] int GetUserSelectedProfile([MarshalAs(UnmanagedType.LPWStr)] out string profileId);
        [PreserveSig] int SetUserSelectedProfile([MarshalAs(UnmanagedType.LPWStr)] string profileId);
    }

    // Windows 11 stores the Focus / Do Not Disturb state in a CloudStore blob whose Data value embeds the
    // active focus profile as a UTF-16 string: "...QuietHoursProfile.Unrestricted" (off),
    // ".PriorityOnly" or ".Alarms" (a Focus / Do Not Disturb mode is on). We read the profile name rather than
    // a fixed byte offset — the binary layout is version-sensitive (an earlier byte-8 heuristic misfired,
    // reading a timestamp byte as the mode), but the profile string is stable. This is what actually flips
    // when you toggle DND in the Action Center.
    //
    // Where the blob lives moved: newer builds (seen on 26200) write it under Store\DefaultAccount\Current, in a
    // "{guid}$windows.data.donotdisturb.quiethourssettings" key, and leave the older Cache\...\notifications key
    // frozen at whatever it last said. So the newer key wins whenever it exists, and the old one is only the
    // fallback for builds that predate it.
    private const string StoreRoot = @"SOFTWARE\Microsoft\Windows\CurrentVersion\CloudStore\Store\";
    private const string DndSettingsSuffix = "$windows.data.donotdisturb.quiethourssettings";
    private const string LegacySettingsKey =
        StoreRoot + @"Cache\DefaultAccount\$$windows.data.notifications.quiethourssettings\Current";

    // The {guid} prefix isn't a constant we can rely on, so the newer key is found by suffix once and cached.
    private string? _dndSettingsKey;

    private bool QuietHoursOn()
    {
        try
        {
            if (ResolveDndSettingsKey() is { } current && ReadProfile(current) is { } on) return on;
            return ReadProfile(LegacySettingsKey) ?? false;
        }
        catch { return false; }   // unknown / unreadable → treat as off, so nothing is wrongly hidden
    }

    private string? ResolveDndSettingsKey()
    {
        if (_dndSettingsKey is not null) return _dndSettingsKey;
        using var current = Registry.CurrentUser.OpenSubKey(StoreRoot + @"DefaultAccount\Current");
        var name = current?.GetSubKeyNames()
            .FirstOrDefault(n => n.EndsWith(DndSettingsSuffix, StringComparison.OrdinalIgnoreCase));
        if (name is null) return null;
        // The blob sits on a same-named child: "{guid}$...quiethourssettings\windows.data.donotdisturb.quiethourssettings".
        return _dndSettingsKey = $@"{StoreRoot}DefaultAccount\Current\{name}\{DndSettingsSuffix[1..]}";
    }

    // true = a quiet profile is active, false = Unrestricted, null = no key / no recognisable profile.
    private static bool? ReadProfile(string keyPath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath);
        if (key?.GetValue("Data") is not byte[] { Length: > 1 } data) return null;
        // The string follows a variable-length timestamp, so it can start on an odd byte: decode both alignments.
        var s = System.Text.Encoding.Unicode.GetString(data) + "\n" + System.Text.Encoding.Unicode.GetString(data, 1, data.Length - 1);
        if (s.Contains("Unrestricted", StringComparison.Ordinal)) return false;   // notifications allowed = off
        if (s.Contains("PriorityOnly", StringComparison.Ordinal)
            || s.Contains("Alarms", StringComparison.Ordinal)) return true;       // a Focus/DND mode is on
        return null;
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

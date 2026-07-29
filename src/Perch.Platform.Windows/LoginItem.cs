using Microsoft.Win32;
using Perch.Data;
using Perch.Platform;

namespace Perch.Platform.Windows;

/// <summary>
/// Windows <see cref="ILoginItem"/>: a value under the per-user run key
/// (<c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>), which needs no elevation and no scheduled
/// task. The value name carries the profile suffix ("Perch" / "Perch (Dev)") so a dev instance registers
/// alongside — never over the top of — an installed Perch, matching every other per-user registration.
///
/// The command is the running executable, quoted, with no <c>--autostarted</c>: a login-started tray is the
/// user's tray, so it must not auto-close after the last session the way a hook-launched one does.
/// Best-effort throughout — a failure just means Perch doesn't start at login.
/// </summary>
public sealed class LoginItem : ILoginItem
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private static string ValueName => "Perch" + AppProfile.DisplaySuffix;

    public void Register()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            key?.SetValue(ValueName, $"\"{exe}\"", RegistryValueKind.String);
        }
        catch { /* best-effort: no login start is survivable */ }
    }

    public void Unregister()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch { /* best-effort */ }
    }

    public bool IsRegistered()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is not null;
        }
        catch { return false; }
    }
}

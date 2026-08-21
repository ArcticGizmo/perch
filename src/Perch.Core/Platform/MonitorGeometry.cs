namespace Perch.Platform;

/// <summary>
/// A monitor's geometry read live from the OS (bypassing any cached screen list): the full bounds and the
/// work area (taskbar/appbars excluded), both in physical pixels, plus the display scale (DPI / 96). Used
/// where the UI framework's cached screen list has proven stale — notably the docked column re-deriving its
/// height after a resolution change, where Avalonia's <c>Screens.WorkingArea</c> can keep reporting the old,
/// larger extent and leave the column drooping under the taskbar.
/// </summary>
public readonly record struct MonitorGeometry(
    int BoundsX, int BoundsY, int BoundsWidth, int BoundsHeight,
    int WorkX, int WorkY, int WorkWidth, int WorkHeight,
    double Scale);

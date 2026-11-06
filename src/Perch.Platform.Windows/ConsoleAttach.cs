using System.Runtime.InteropServices;
using System.Text;

namespace Perch.Platform.Windows;

/// <summary>
/// Briefly binds Perch to another process's console, to read (or, for tab finding, tag) what only the console
/// object knows: its window and its title. Shared by <see cref="WindowActivator"/> (which terminal window hosts
/// a session) and <see cref="WindowsTerminalTabs"/> (which tab).
/// <para>
/// Console attachment is process-<em>global</em> state, so every attach serialises on one lock, makes only a
/// few quick console calls, and always <c>FreeConsole()</c>s in a finally. Perch's Windows head is a WinExe
/// (GUI, no console of its own), so FreeConsole/AttachConsole neither allocate nor destroy a visible console
/// window; the leading FreeConsole only matters for a dev run that inherited the launching terminal's console
/// (AttachConsole fails with ERROR_ACCESS_DENIED while we already hold one).
/// </para>
/// <para>
/// While attached, Perch is one of that console's processes, so a Ctrl+C or Ctrl+Break typed into the session
/// at that instant would reach Perch too, and the default handler ends the process. <see cref="SwallowCtrl"/>
/// absorbs them for the duration of the attach. It is a registered handler rather than
/// <c>SetConsoleCtrlHandler(null, true)</c> because that flag is inherited by any child process Perch starts
/// meanwhile (a new session, a git call), which would then ignore Ctrl+C for good.
/// </para>
/// </summary>
internal static class ConsoleAttach
{
    private static readonly object _lock = new();

    /// <summary>
    /// Runs <paramref name="read"/> while attached to <paramref name="pid"/>'s console; default(T) when the
    /// attach fails (the process is gone, has no Win32 console as under mintty, or runs at a higher integrity
    /// level than Perch).
    /// </summary>
    public static T? With<T>(int pid, Func<T> read)
    {
        lock (_lock)
        {
            FreeConsole();
            SetConsoleCtrlHandler(SwallowCtrl, true);
            try
            {
                if (!AttachConsole((uint)pid))
                    return default;
                try
                {
                    return read();
                }
                finally
                {
                    FreeConsole();
                }
            }
            finally
            {
                SetConsoleCtrlHandler(SwallowCtrl, false);
            }
        }
    }

    /// <summary>The attached console's title; null when empty or unreadable. Only valid inside <see cref="With{T}"/>.</summary>
    public static string? Title(int capacity = 1024)
    {
        var sb = new StringBuilder(capacity);
        return GetConsoleTitleW(sb, (uint)sb.Capacity) == 0 ? null : sb.ToString();
    }

    /// <summary>Sets the attached console's title. Only valid inside <see cref="With{T}"/>.</summary>
    public static bool SetTitle(string title) => SetConsoleTitleW(title);

    // A static field so the marshalled delegate outlives every native call that can invoke it.
    private static readonly ConsoleCtrlHandler SwallowCtrl =
        ctrlType => ctrlType is CTRL_C_EVENT or CTRL_BREAK_EVENT;

    private const uint CTRL_C_EVENT = 0;
    private const uint CTRL_BREAK_EVENT = 1;

    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool ConsoleCtrlHandler(uint ctrlType);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleCtrlHandler(
        ConsoleCtrlHandler handler, [MarshalAs(UnmanagedType.Bool)] bool add);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint dwProcessId);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetConsoleTitleW(StringBuilder lpConsoleTitle, uint nSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleTitleW(string lpConsoleTitle);
}

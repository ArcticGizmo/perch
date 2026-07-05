using System.Runtime.InteropServices;
using Perch.Platform;

namespace Perch.Platform.Windows;

/// <summary>
/// Windows <see cref="IGlobalHotkey"/> via <c>RegisterHotKey</c>/<c>WM_HOTKEY</c>. Registering with a
/// null window handle posts <c>WM_HOTKEY</c> to the calling thread's message queue, so this owns a
/// dedicated background thread that registers the combo and runs a <c>GetMessage</c> loop to receive it —
/// self-contained, with no dependency on the host UI toolkit's message pump. The press callback is
/// invoked on that thread; the caller marshals to its UI thread. Dispose posts <c>WM_QUIT</c> to end the
/// loop, which unregisters the hotkey.
/// </summary>
public sealed class GlobalHotkey : IGlobalHotkey
{
    private const int  WM_HOTKEY    = 0x0312;
    private const uint WM_QUIT      = 0x0012;
    private const uint MOD_ALT      = 0x0001;
    private const uint MOD_CONTROL  = 0x0002;
    private const uint MOD_SHIFT    = 0x0004;
    private const uint MOD_NOREPEAT = 0x4000; // don't auto-repeat while the keys are held
    private const int  HotkeyId     = 0xB001;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint   message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint   time;
        public int    ptX;
        public int    ptY;
    }

    private Thread? _thread;
    private uint _threadId;
    private Action? _onPressed;
    private volatile bool _disposed;

    public bool Register(HotkeyModifiers modifiers, char key, Action onPressed)
    {
        if (_thread != null) throw new InvalidOperationException("This hotkey is already registered.");

        _onPressed = onPressed;
        uint vk = char.ToUpperInvariant(key);
        uint mods = MOD_NOREPEAT
            | ((modifiers & HotkeyModifiers.Alt)     != 0 ? MOD_ALT     : 0)
            | ((modifiers & HotkeyModifiers.Control) != 0 ? MOD_CONTROL : 0)
            | ((modifiers & HotkeyModifiers.Shift)   != 0 ? MOD_SHIFT   : 0);

        // The message loop and the RegisterHotKey call must share one thread (WM_HOTKEY lands on the
        // thread that registered it). Block until that thread reports whether the OS accepted the combo.
        using var ready = new ManualResetEventSlim();
        bool registered = false;

        _thread = new Thread(() =>
        {
            _threadId = GetCurrentThreadId();
            registered = RegisterHotKey(IntPtr.Zero, HotkeyId, mods, vk);
            ready.Set();
            if (!registered) return;

            // GetMessage returns >0 for a message, 0 on WM_QUIT (posted by Dispose), −1 on error.
            while (!_disposed && GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.message == WM_HOTKEY && msg.wParam.ToInt32() == HotkeyId)
                    _onPressed?.Invoke();
            }

            UnregisterHotKey(IntPtr.Zero, HotkeyId);
        })
        {
            IsBackground = true,
            Name = "PerchGlobalHotkey",
        };
        _thread.Start();
        ready.Wait();
        return registered;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_threadId != 0) PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread?.Join(500);
    }
}

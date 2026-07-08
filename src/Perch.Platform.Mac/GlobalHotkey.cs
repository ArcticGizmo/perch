using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Perch.Platform;

namespace Perch.Platform.Mac;

/// <summary>
/// macOS <see cref="IGlobalHotkey"/>: a process-wide shortcut via Carbon <c>RegisterEventHotKey</c>, which
/// fires even when Perch has no focused window and — unlike an <c>NSEvent</c> global monitor — needs no
/// Accessibility/Input-Monitoring permission. One application-wide Carbon event handler is installed lazily
/// and dispatches to the registered <see cref="Action"/> by hot-key id; the handler runs on the main
/// (run-loop) thread, and the caller marshals to its UI thread as the interface documents.
///
/// NOTE (Phase 3): written against the Carbon Event Manager docs but not yet verified on a Mac. Only
/// letters and digits are mappable to virtual key codes here (all the hotkeys Perch uses); an unmappable
/// key makes <see cref="Register"/> return false, which the caller treats as a refused binding.
/// </summary>
public sealed class GlobalHotkey : IGlobalHotkey
{
    private const string Carbon = "/System/Library/Frameworks/Carbon.framework/Carbon";

    // Classic Carbon modifier masks used by RegisterEventHotKey (not the Cocoa NSEventModifierFlags).
    private const uint shiftKey = 0x0200, optionKey = 0x0800, controlKey = 0x1000;

    private static readonly uint kEventClassKeyboard = FourCC("keyb");
    private static readonly uint kEventParamDirectObject = FourCC("----");
    private static readonly uint typeEventHotKeyID = FourCC("hkid");
    private const uint kEventHotKeyPressed = 5;
    private static readonly uint HotKeySignature = FourCC("PRCH");

    private static readonly ConcurrentDictionary<uint, Action> Actions = new();
    private static int _nextId;
    private static IntPtr _appHandlerRef;
    private static readonly object InstallGate = new();
    // Held for the process lifetime so the function pointer handed to Carbon stays valid.
    private static readonly EventHandlerProc HandlerProc = HandleEvent;

    private IntPtr _hotKeyRef;
    private uint _id;

    public bool Register(HotkeyModifiers modifiers, char key, Action onPressed)
    {
        if (!TryKeyCode(key, out uint code)) return false;

        uint mods = 0;
        if (modifiers.HasFlag(HotkeyModifiers.Alt)) mods |= optionKey;
        if (modifiers.HasFlag(HotkeyModifiers.Control)) mods |= controlKey;
        if (modifiers.HasFlag(HotkeyModifiers.Shift)) mods |= shiftKey;

        if (!EnsureHandlerInstalled()) return false;

        uint id = (uint)Interlocked.Increment(ref _nextId);
        var hkId = new EventHotKeyID { signature = HotKeySignature, id = id };
        int status = RegisterEventHotKey(code, mods, hkId, GetApplicationEventTarget(), 0, out IntPtr href);
        if (status != 0 || href == IntPtr.Zero) return false;

        _hotKeyRef = href;
        _id = id;
        Actions[id] = onPressed;
        return true;
    }

    private static bool EnsureHandlerInstalled()
    {
        lock (InstallGate)
        {
            if (_appHandlerRef != IntPtr.Zero) return true;
            var spec = new[] { new EventTypeSpec { eventClass = kEventClassKeyboard, eventKind = kEventHotKeyPressed } };
            IntPtr fp = Marshal.GetFunctionPointerForDelegate(HandlerProc);
            if (InstallEventHandler(GetApplicationEventTarget(), fp, 1, spec, IntPtr.Zero, out IntPtr href) != 0)
                return false;
            _appHandlerRef = href;
            return true;
        }
    }

    private static int HandleEvent(IntPtr callRef, IntPtr evt, IntPtr userData)
    {
        try
        {
            if (GetEventParameter(evt, kEventParamDirectObject, typeEventHotKeyID, IntPtr.Zero,
                    (uint)Marshal.SizeOf<EventHotKeyID>(), IntPtr.Zero, out EventHotKeyID hk) == 0
                && Actions.TryGetValue(hk.id, out var action))
            {
                action();
            }
        }
        catch { /* never let an exception escape into the Carbon event loop */ }
        return 0; // noErr
    }

    public void Dispose()
    {
        if (_hotKeyRef != IntPtr.Zero)
        {
            try { UnregisterEventHotKey(_hotKeyRef); } catch { }
            _hotKeyRef = IntPtr.Zero;
        }
        if (_id != 0) { Actions.TryRemove(_id, out _); _id = 0; }
    }

    private static uint FourCC(string s) =>
        ((uint)s[0] << 24) | ((uint)s[1] << 16) | ((uint)s[2] << 8) | s[3];

    // ANSI virtual key codes (kVK_ANSI_*) for letters and digits — the keys Perch's shortcuts use.
    private static bool TryKeyCode(char key, out uint code)
    {
        code = char.ToUpperInvariant(key) switch
        {
            'A' => 0, 'S' => 1, 'D' => 2, 'F' => 3, 'H' => 4, 'G' => 5, 'Z' => 6, 'X' => 7, 'C' => 8,
            'V' => 9, 'B' => 11, 'Q' => 12, 'W' => 13, 'E' => 14, 'R' => 15, 'Y' => 16, 'T' => 17,
            'O' => 31, 'U' => 32, 'I' => 34, 'P' => 35, 'L' => 37, 'J' => 38, 'K' => 40, 'N' => 45, 'M' => 46,
            '1' => 18, '2' => 19, '3' => 20, '4' => 21, '5' => 23, '6' => 22, '7' => 26, '8' => 28,
            '9' => 25, '0' => 29,
            _ => uint.MaxValue,
        };
        return code != uint.MaxValue;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int EventHandlerProc(IntPtr callRef, IntPtr evt, IntPtr userData);

    [StructLayout(LayoutKind.Sequential)]
    private struct EventHotKeyID { public uint signature; public uint id; }

    [StructLayout(LayoutKind.Sequential)]
    private struct EventTypeSpec { public uint eventClass; public uint eventKind; }

    [DllImport(Carbon)] private static extern IntPtr GetApplicationEventTarget();
    [DllImport(Carbon)] private static extern int RegisterEventHotKey(uint keyCode, uint modifiers, EventHotKeyID id, IntPtr target, uint options, out IntPtr outRef);
    [DllImport(Carbon)] private static extern int UnregisterEventHotKey(IntPtr hotKeyRef);
    [DllImport(Carbon)] private static extern int InstallEventHandler(IntPtr target, IntPtr handler, uint numTypes, EventTypeSpec[] typeList, IntPtr userData, out IntPtr outRef);
    [DllImport(Carbon)] private static extern int GetEventParameter(IntPtr evt, uint name, uint desiredType, IntPtr outActualType, uint bufSize, IntPtr outActualSize, out EventHotKeyID outData);
}

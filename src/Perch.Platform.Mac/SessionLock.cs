using System.Runtime.InteropServices;
using Perch.Platform;

namespace Perch.Platform.Mac;

/// <summary>
/// macOS <see cref="ISessionLock"/>: reports whether the screen is locked by reading the
/// <c>CGSSessionScreenIsLocked</c> flag out of <c>CGSessionCopyCurrentDictionary()</c>. The flag's CF
/// value type has varied across OS versions (CFNumber vs CFBoolean), so both are handled via a
/// <c>CFGetTypeID</c> check; anything else (or an absent key) reads as unlocked.
///
/// Polls on demand from the getter rather than subscribing to the (undocumented)
/// <c>com.apple.screenIsLocked</c>/<c>Unlocked</c> distributed notifications — the notification dispatcher
/// only reads <see cref="IsLocked"/> when it's about to send an external push, so a per-event query is
/// cheap and avoids wiring an Objective-C observer. <see cref="Dispose"/> is therefore a no-op.
///
/// NOTE (Phase 3): written against the (undocumented) Quartz session API but not yet verified on a Mac.
/// A safe default falls out of the design — any failure path returns "unlocked", which never suppresses a
/// notification the user would otherwise get.
/// </summary>
public sealed class SessionLock : ISessionLock
{
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const uint kCFStringEncodingUTF8 = 0x08000100;
    private const nint kCFNumberSInt32Type = 3;

    public bool IsLocked
    {
        get
        {
            try { return QueryLocked(); }
            catch { return false; } // best-effort: never suppress a notification because the query failed
        }
    }

    private static bool QueryLocked()
    {
        IntPtr dict = CGSessionCopyCurrentDictionary(); // +1 retained — we own it
        if (dict == IntPtr.Zero) return false;
        IntPtr key = IntPtr.Zero;
        try
        {
            key = CFStringCreateWithCString(IntPtr.Zero, "CGSSessionScreenIsLocked", kCFStringEncodingUTF8);
            if (key == IntPtr.Zero) return false;

            IntPtr val = CFDictionaryGetValue(dict, key); // borrowed — do not release
            if (val == IntPtr.Zero) return false;

            nuint typeId = CFGetTypeID(val);
            if (typeId == CFBooleanGetTypeID())
                return CFBooleanGetValue(val) != 0;
            if (typeId == CFNumberGetTypeID() && CFNumberGetValue(val, kCFNumberSInt32Type, out int n) != 0)
                return n != 0;
            return false;
        }
        finally
        {
            if (key != IntPtr.Zero) CFRelease(key);
            CFRelease(dict);
        }
    }

    public void Dispose() { }

    // ── P/Invoke ────────────────────────────────────────────────────────────────────
    [DllImport(CoreGraphics)]
    private static extern IntPtr CGSessionCopyCurrentDictionary();

    [DllImport(CoreFoundation)]
    private static extern IntPtr CFStringCreateWithCString(IntPtr alloc, [MarshalAs(UnmanagedType.LPStr)] string cStr, uint encoding);

    [DllImport(CoreFoundation)]
    private static extern IntPtr CFDictionaryGetValue(IntPtr dict, IntPtr key);

    [DllImport(CoreFoundation)]
    private static extern nuint CFGetTypeID(IntPtr cf);

    [DllImport(CoreFoundation)]
    private static extern nuint CFBooleanGetTypeID();

    [DllImport(CoreFoundation)]
    private static extern byte CFBooleanGetValue(IntPtr boolean);

    [DllImport(CoreFoundation)]
    private static extern nuint CFNumberGetTypeID();

    [DllImport(CoreFoundation)]
    private static extern byte CFNumberGetValue(IntPtr number, nint theType, out int value);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(IntPtr cf);
}

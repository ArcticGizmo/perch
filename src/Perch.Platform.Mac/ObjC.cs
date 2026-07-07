using System.Runtime.InteropServices;

namespace Perch.Platform.Mac;

/// <summary>
/// Minimal Objective-C runtime interop shared by the macOS platform implementations: class/selector
/// lookup plus a handful of typed <c>objc_msgSend</c> overloads.
///
/// <para><c>objc_msgSend</c> is <em>not</em> variadic on arm64 — each message send must be P/Invoked with
/// the exact argument types of the real method — so we declare one overload per signature we actually use
/// rather than a single catch-all. Two Darwin-specific gotchas are baked in here:</para>
/// <list type="bullet">
/// <item>BOOL is a 1-byte signed char, so bool arguments are passed as <see cref="byte"/> (0/1), never a
/// managed <c>bool</c> (which P/Invoke would widen to a 4-byte Win32 BOOL).</item>
/// <item>Struct-return methods are deliberately avoided — x86_64 needs <c>objc_msgSend_stret</c> for them
/// while arm64 does not, so keeping to id/void/primitive returns stays portable across both arches.</item>
/// </list>
///
/// NOTE (Phase 3): compiles anywhere (libobjc only resolves at runtime on macOS); not yet verified on a Mac.
/// </summary>
internal static class ObjC
{
    private const string Lib = "/usr/lib/libobjc.dylib";

    [DllImport(Lib)]
    public static extern IntPtr objc_getClass([MarshalAs(UnmanagedType.LPStr)] string name);

    [DllImport(Lib)]
    public static extern IntPtr sel_registerName([MarshalAs(UnmanagedType.LPStr)] string name);

    /// <summary>id (*)(id, SEL) — a plain message send returning an object/pointer.</summary>
    [DllImport(Lib, EntryPoint = "objc_msgSend")]
    public static extern IntPtr SendGet(IntPtr receiver, IntPtr selector);

    /// <summary>id (*)(id, SEL, id) — a message send taking one object/pointer arg, returning one.</summary>
    [DllImport(Lib, EntryPoint = "objc_msgSend")]
    public static extern IntPtr SendGet(IntPtr receiver, IntPtr selector, IntPtr arg);

    /// <summary>id (*)(id, SEL, int) — e.g. +runningApplicationWithProcessIdentifier: (pid_t is int).</summary>
    [DllImport(Lib, EntryPoint = "objc_msgSend")]
    public static extern IntPtr SendGet(IntPtr receiver, IntPtr selector, int arg);

    /// <summary>id (*)(id, SEL, const void*, NSUInteger) — e.g. +[NSData dataWithBytes:length:].</summary>
    [DllImport(Lib, EntryPoint = "objc_msgSend")]
    public static extern IntPtr SendGet(IntPtr receiver, IntPtr selector, IntPtr bytes, nuint length);

    /// <summary>id (*)(id, SEL, const char*) — e.g. +[NSString stringWithUTF8String:] (UTF8 C string).</summary>
    [DllImport(Lib, EntryPoint = "objc_msgSend")]
    public static extern IntPtr SendGetUtf8(IntPtr receiver, IntPtr selector,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string utf8);

    /// <summary>BOOL (*)(id, SEL, id, id) — e.g. -[NSPasteboard setData:forType:]. Non-zero is YES.</summary>
    [DllImport(Lib, EntryPoint = "objc_msgSend")]
    public static extern byte SendBool(IntPtr receiver, IntPtr selector, IntPtr arg1, IntPtr arg2);

    /// <summary>NSInteger (*)(id, SEL) — a message send returning an NSInteger (e.g. -activationPolicy).</summary>
    [DllImport(Lib, EntryPoint = "objc_msgSend")]
    public static extern nint SendNint(IntPtr receiver, IntPtr selector);

    /// <summary>BOOL (*)(id, SEL, SEL/id) — a message send taking one pointer arg and returning a Darwin
    /// BOOL (1-byte signed char), e.g. -respondsToSelector: / -isKindOfClass:. Returns non-zero for YES.</summary>
    [DllImport(Lib, EntryPoint = "objc_msgSend")]
    public static extern byte SendBool(IntPtr receiver, IntPtr selector, IntPtr arg);

    /// <summary>void (*)(id, SEL) — a no-argument message send returning nothing (e.g. -orderFrontRegardless).</summary>
    [DllImport(Lib, EntryPoint = "objc_msgSend")]
    public static extern void SendVoid(IntPtr receiver, IntPtr selector);

    /// <summary>void (*)(id, SEL, NSInteger).</summary>
    [DllImport(Lib, EntryPoint = "objc_msgSend")]
    public static extern void SendVoid(IntPtr receiver, IntPtr selector, nint arg);

    /// <summary>void (*)(id, SEL, NSUInteger).</summary>
    [DllImport(Lib, EntryPoint = "objc_msgSend")]
    public static extern void SendVoid(IntPtr receiver, IntPtr selector, nuint arg);

    /// <summary>void (*)(id, SEL, BOOL) — BOOL is a 1-byte signed char on Darwin.</summary>
    [DllImport(Lib, EntryPoint = "objc_msgSend")]
    public static extern void SendVoid(IntPtr receiver, IntPtr selector, byte arg);

    /// <summary>Send a selector that returns an <c>NSString</c> and marshal it to a managed string via
    /// <c>-UTF8String</c>. Null when the receiver or either result is nil (e.g. an app with no bundle id).</summary>
    public static string? SendString(IntPtr receiver, IntPtr selector)
    {
        if (receiver == IntPtr.Zero) return null;
        IntPtr nsString = SendGet(receiver, selector);
        if (nsString == IntPtr.Zero) return null;
        IntPtr utf8 = SendGet(nsString, sel_registerName("UTF8String"));
        return utf8 == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(utf8);
    }

    public static IntPtr Sel(string name) => sel_registerName(name);
    public static IntPtr Class(string name) => objc_getClass(name);
}

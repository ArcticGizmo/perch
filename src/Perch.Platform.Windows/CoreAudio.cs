using System.Runtime.InteropServices;

namespace Perch.Platform.Windows;

/// <summary>
/// The slice of Windows Core Audio (WASAPI) Perch needs to answer "is anything capturing right now, and
/// which process is it" and to drive the capture device's own mute. Hand-written COM interop rather than a
/// NuGet dependency: it's a handful of interfaces, it keeps <c>Perch.Platform.Windows</c> dependency-free
/// like its siblings, and the vtable order below is the contract — <b>do not reorder the members</b>, each
/// interface must list its inherited methods first, in exactly the documented order.
///
/// Only <see cref="MicrophoneMonitor"/> uses this; nothing here knows about any particular application.
/// </summary>
internal static class CoreAudio
{
    internal const uint DEVICE_STATE_ACTIVE = 0x1;
    internal const uint CLSCTX_ALL = 23;
    internal const uint STGM_READ = 0;

    /// <summary>The "which default device" role. Capture defaults can differ per role, and a call app takes
    /// the communications one — so that's the endpoint to name and to mute when nothing is capturing yet.</summary>
    internal const int Role_Communications = 2;

    /// <summary><c>PKEY_Device_FriendlyName</c> — the endpoint's display name ("Microphone (Logitech …)").</summary>
    internal static PROPERTYKEY FriendlyNameKey => new()
    {
        fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"),
        pid = 14,
    };

    /// <summary>Best-effort COM release — these objects are enumerated in tight loops on a polling tick, so
    /// letting the GC collect the RCWs at its leisure would hold audio-engine objects alive far longer than
    /// necessary. Never throws.</summary>
    internal static void Release(object? com)
    {
        try
        {
            if (com is not null && Marshal.IsComObject(com)) Marshal.ReleaseComObject(com);
        }
        catch { /* already released / not an RCW */ }
    }

    internal enum EDataFlow { eRender, eCapture, eAll }

    /// <summary>A capture session's state. <c>Active</c> is the only one that means audio is flowing —
    /// a session lingers as <c>Inactive</c> long after a call ends.</summary>
    internal enum AudioSessionState { Inactive, Active, Expired }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PROPERTYKEY
    {
        public Guid fmtid;
        public int pid;
    }

    // Only the string case is ever read (PKEY_Device_FriendlyName is a VT_LPWSTR), so the union is modelled
    // as padding plus the pointer rather than fully marshalled.
    [StructLayout(LayoutKind.Sequential)]
    internal struct PROPVARIANT
    {
        public ushort vt;
        private ushort r1, r2, r3;
        public IntPtr pwszVal;
        private IntPtr r4;
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    internal class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(EDataFlow flow, uint stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow flow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int Item(int index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        [PreserveSig] int OpenPropertyStore(uint access, out IPropertyStore store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out uint state);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPropertyStore
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetAt(int index, out PROPERTYKEY key);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
        [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PROPVARIANT value);
        [PreserveSig] int Commit();
    }

    // IAudioSessionManager2 : IAudioSessionManager — the two inherited members come first.
    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(ref Guid groupingParam, uint flags, out IAudioSessionControl2 control);
        [PreserveSig] int GetSimpleAudioVolume(ref Guid groupingParam, uint flags, out IntPtr volume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
        [PreserveSig] int RegisterSessionNotification(IntPtr notification);
        [PreserveSig] int UnregisterSessionNotification(IntPtr notification);
        [PreserveSig] int RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string? sessionId, IntPtr notification);
        [PreserveSig] int UnregisterDuckNotification(IntPtr notification);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, out IAudioSessionControl2 control);
    }

    // IAudioSessionControl2 : IAudioSessionControl — the nine inherited members come first. Declared as the
    // "2" interface throughout so GetProcessId is reachable without a separate QueryInterface.
    [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionControl2
    {
        [PreserveSig] int GetState(out AudioSessionState state);
        [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid eventContext);
        [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid eventContext);
        [PreserveSig] int GetGroupingParam(out Guid groupingParam);
        [PreserveSig] int SetGroupingParam(ref Guid groupingParam, ref Guid eventContext);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr notification);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr notification);
        [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetProcessId(out uint pid);
        [PreserveSig] int IsSystemSoundsSession();
        [PreserveSig] int SetDuckingPreference(bool optOut);
    }

}

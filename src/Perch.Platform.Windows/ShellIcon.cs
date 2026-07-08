using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Perch.Platform.Windows;

/// <summary>
/// Loads application icons through the Windows shell — the same pipeline Explorer and the Start Menu use
/// to paint icons. A shared copy of the WinForms app's ShellIcon, kept here so the Avalonia head can
/// resolve icons through <see cref="WindowsAppIconProvider"/> without depending on WinForms. Two entry
/// points:
/// <list type="bullet">
/// <item><see cref="LoadStartMenuByName"/> looks the app up by its Start Menu display name — the only
/// reliable way to get a Microsoft Store / MSIX app's real logo (their 0-byte alias exe carries only a
/// generic placeholder; the true logo lives in the package and is reachable via shell:AppsFolder).</item>
/// <item><see cref="Load"/> renders the icon for a file-system path (an ordinary executable).</item>
/// </list>
/// Both return a true-colour bitmap with transparency at (most) the requested size; the caller owns it.
/// </summary>
internal static class ShellIcon
{
    // Renders the icon for an executable (or any file) addressed by path. Null if the shell can't.
    public static Bitmap? Load(string? path, int size)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        object? o = null;
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out o);
            return o is IShellItemImageFactory factory ? ImageFromFactory(factory, size) : null;
        }
        catch { return null; }
        finally { if (o != null) Marshal.ReleaseComObject(o); }
    }

    // Renders the icon Windows shows for the app whose Start Menu display name equals (case-
    // insensitively) the given name. Resolves the app directly by its cached AUMID, so it doesn't
    // re-enumerate the (slow, ~1s+) AppsFolder. Null when no app matches or the shell can't render one.
    public static Bitmap? LoadStartMenuByName(string? name, int size)
    {
        var aumid = StartMenuAppId(name);
        if (aumid == null) return null;
        object? o = null;
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            SHCreateItemFromParsingName("shell:AppsFolder\\" + aumid, IntPtr.Zero, ref iid, out o);
            return o is IShellItemImageFactory f ? ImageFromFactory(f, size) : null;
        }
        catch { return null; }
        finally { if (o != null) Marshal.ReleaseComObject(o); }
    }

    // The AppUserModelID of the matching Start Menu app, suitable for launching via
    // "shell:AppsFolder\<id>". Null when no app matches that display name.
    public static string? StartMenuAppId(string? name) =>
        !string.IsNullOrWhiteSpace(name) && AppMap().TryGetValue(name.Trim(), out var id) ? id : null;

    // Cached display-name → AUMID map of every Start Menu app. Enumerating the AppsFolder is slow
    // (~1s+), so it's done once and reused for the process lifetime; installs/uninstalls mid-session
    // aren't picked up until restart, which is an acceptable trade for keeping list edits snappy.
    private static volatile Dictionary<string, string>? _appMap;
    private static readonly object _appMapLock = new();

    private static Dictionary<string, string> AppMap()
    {
        var map = _appMap;
        if (map != null) return map;
        lock (_appMapLock)
            return _appMap ??= BuildAppMap();
    }

    private static Dictionary<string, string> BuildAppMap()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        object? appsObj = null, enumObj = null;
        try
        {
            var iidItem = typeof(IShellItem).GUID;
            SHCreateItemFromParsingName("shell:AppsFolder", IntPtr.Zero, ref iidItem, out appsObj);
            if (appsObj is not IShellItem apps) return map;

            var bhid    = BHID_EnumItems;
            var iidEnum = typeof(IEnumShellItems).GUID;
            if (apps.BindToHandler(IntPtr.Zero, ref bhid, ref iidEnum, out enumObj) != 0 ||
                enumObj is not IEnumShellItems items)
                return map;

            while (items.Next(1, out var item, out uint fetched) == 0 && fetched == 1)
            {
                try
                {
                    string? name  = item.GetDisplayName(SIGDN_NORMALDISPLAY,         out IntPtr pN) == 0 ? Consume(pN) : null;
                    string? aumid = item.GetDisplayName(SIGDN_PARENTRELATIVEPARSING, out IntPtr pId) == 0 ? Consume(pId) : null;
                    if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(aumid) && !map.ContainsKey(name))
                        map[name] = aumid;  // keep the first of any duplicate display names
                }
                finally { Marshal.ReleaseComObject(item); }
            }
        }
        catch { /* return whatever we gathered */ }
        finally
        {
            if (enumObj != null) Marshal.ReleaseComObject(enumObj);
            if (appsObj != null) Marshal.ReleaseComObject(appsObj);
        }
        return map;
    }

    // Reads and frees a CoTaskMem-allocated LPWSTR returned by GetDisplayName.
    private static string? Consume(IntPtr pwsz)
    {
        var s = Marshal.PtrToStringUni(pwsz);
        Marshal.FreeCoTaskMem(pwsz);
        return s;
    }

    private static Bitmap? ImageFromFactory(IShellItemImageFactory factory, int size)
    {
        IntPtr hbitmap = IntPtr.Zero;
        try
        {
            // RESIZETOFIT keeps the result within the box; ICONONLY asks for the program icon rather
            // than a file-content thumbnail.
            factory.GetImage(new SIZE(size, size), SIIGBF_ICONONLY | SIIGBF_RESIZETOFIT, out hbitmap);
            return hbitmap == IntPtr.Zero ? null : FromHBitmapWithAlpha(hbitmap);
        }
        finally { if (hbitmap != IntPtr.Zero) DeleteObject(hbitmap); }
    }

    // Copies a 32bpp premultiplied DIB HBITMAP into a managed bitmap, keeping the alpha channel that
    // Image.FromHbitmap discards. Falls back to FromHbitmap for any non-32bpp result.
    private static Bitmap FromHBitmapWithAlpha(IntPtr hbitmap)
    {
        var bm = new BITMAP();
        int read = GetObject(hbitmap, Marshal.SizeOf<BITMAP>(), ref bm);
        if (read == 0 || bm.bmBitsPixel != 32 || bm.bmBits == IntPtr.Zero || bm.bmWidth <= 0 || bm.bmHeight <= 0)
            return Image.FromHbitmap(hbitmap);

        int w = bm.bmWidth, h = bm.bmHeight;
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);  // shell hands back premultiplied BGRA
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
        try
        {
            int srcStride = w * 4;  // top-down 32bpp DIB
            for (int y = 0; y < h; y++)
                CopyMemory(data.Scan0 + y * data.Stride, bm.bmBits + y * srcStride, (uint)srcStride);
        }
        finally { bmp.UnlockBits(data); }
        return bmp;
    }

    // ── Interop ───────────────────────────────────────────────────────────────
    private const int SIIGBF_RESIZETOFIT  = 0x0;
    private const int SIIGBF_ICONONLY     = 0x4;
    private const int SIGDN_NORMALDISPLAY = 0x0;
    private const int SIGDN_PARENTRELATIVEPARSING = unchecked((int)0x80018001);

    // BHID_EnumItems — binds a shell folder item to an enumerator over its children.
    private static readonly Guid BHID_EnumItems = new("94f60519-2850-4924-aa5a-d15e84868039");

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx, cy;
        public SIZE(int cx, int cy) { this.cx = cx; this.cy = cy; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public ushort bmPlanes, bmBitsPixel;
        public IntPtr bmBits;
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        void GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid,
                                        [MarshalAs(UnmanagedType.Interface)] out object ppv);
        [PreserveSig] int GetParent(out IShellItem ppsi);
        [PreserveSig] int GetDisplayName(int sigdnName, out IntPtr ppszName);
        [PreserveSig] int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        [PreserveSig] int Compare(IShellItem psi, uint hint, out int piOrder);
    }

    [ComImport, Guid("70629033-e363-4a28-a567-0db78006e6d7"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumShellItems
    {
        [PreserveSig] int Next(uint celt, out IShellItem rgelt, out uint pceltFetched);
        [PreserveSig] int Skip(uint celt);
        [PreserveSig] int Reset();
        [PreserveSig] int Clone(out IEnumShellItems ppenum);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        string pszPath, IntPtr pbc, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object ppv);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr hObject, int nCount, ref BITMAP lpObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory")]
    private static extern void CopyMemory(IntPtr dest, IntPtr src, uint count);
}

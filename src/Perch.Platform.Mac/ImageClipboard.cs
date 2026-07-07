using System.Runtime.InteropServices;
using Perch.Data;
using Perch.Platform;

namespace Perch.Platform.Mac;

/// <summary>
/// macOS <see cref="IImageClipboard"/>: puts the Wrapped poster on the general <c>NSPasteboard</c> as PNG
/// (UTI <c>public.png</c>), which every mac app (Messages, Notes, Mail, browsers, image editors) accepts on
/// paste. The pixels are encoded to PNG in portable managed code (<see cref="PngEncoder"/>); the only native
/// work here is handing the resulting bytes to AppKit via the shared <see cref="ObjC"/> interop — building
/// an <c>NSData</c>, clearing the pasteboard, and setting the data for the PNG type.
///
/// Best-effort: any failure (interop, allocation, pasteboard busy) returns false so the Wrapped card shows
/// "Copy unavailable" rather than claiming a success it didn't achieve. Never throws.
/// </summary>
public sealed class ImageClipboard : IImageClipboard
{
    public bool TryCopyBgra(byte[] bgra, int width, int height, int stride)
    {
        if (bgra is null || width <= 0 || height <= 0 || stride < width * 4) return false;
        try
        {
            byte[] png = PngEncoder.FromBgra(bgra, width, height, stride);
            return WritePngToPasteboard(png);
        }
        catch { return false; }
    }

    private static bool WritePngToPasteboard(byte[] png)
    {
        // Copy the PNG into unmanaged memory for +[NSData dataWithBytes:length:], which copies it again
        // into an autoreleased NSData — so the buffer is ours to free immediately after.
        IntPtr buf = Marshal.AllocHGlobal(png.Length);
        try
        {
            Marshal.Copy(png, 0, buf, png.Length);
            IntPtr data = ObjC.SendGet(ObjC.Class("NSData"), ObjC.Sel("dataWithBytes:length:"), buf, (nuint)png.Length);
            if (data == IntPtr.Zero) return false;

            IntPtr pb = ObjC.SendGet(ObjC.Class("NSPasteboard"), ObjC.Sel("generalPasteboard"));
            if (pb == IntPtr.Zero) return false;

            // clearContents returns the new change count (NSInteger); we don't need it. Must be called
            // before writing, or the write is rejected against the previous owner's declared types.
            ObjC.SendVoid(pb, ObjC.Sel("clearContents"));

            IntPtr type = ObjC.SendGetUtf8(ObjC.Class("NSString"), ObjC.Sel("stringWithUTF8String:"), "public.png");
            if (type == IntPtr.Zero) return false;

            return ObjC.SendBool(pb, ObjC.Sel("setData:forType:"), data, type) != 0;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }
}

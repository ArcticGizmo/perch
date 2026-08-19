using System.Runtime.InteropServices;
using Perch.Platform;

namespace Perch.Platform.Windows;

/// <summary>
/// Windows <see cref="ISecretStore"/>: the portable <see cref="FileSecretStore"/> layout, but with each
/// value encrypted at rest by <b>DPAPI</b> under the current user (<see cref="DpapiSecretProtector"/>), so a
/// stored OAuth refresh token is unreadable by another user on the machine and useless if the file is
/// copied elsewhere. DPAPI is reached by P/Invoke into <c>crypt32</c> (no extra package), current-user
/// scope, UI suppressed.
/// </summary>
public sealed class WindowsSecretStore : FileSecretStore
{
    public WindowsSecretStore() : base(new DpapiSecretProtector()) { }
}

/// <summary>
/// DPAPI protector (current-user scope). <c>CryptProtectData</c>/<c>CryptUnprotectData</c> tie the ciphertext
/// to the logged-in user account, so only they — on this machine — can decrypt it. <c>CRYPTPROTECT_UI_FORBIDDEN</c>
/// guarantees no prompt is ever shown (this runs unattended).
/// </summary>
public sealed class DpapiSecretProtector : ISecretProtector
{
    private const uint CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    public byte[] Protect(byte[] plaintext) => Run(plaintext, encrypt: true);
    public byte[] Unprotect(byte[] ciphertext) => Run(ciphertext, encrypt: false);

    private static byte[] Run(byte[] input, bool encrypt)
    {
        var inBlob = default(DATA_BLOB);
        var outBlob = default(DATA_BLOB);
        var pin = GCHandle.Alloc(input, GCHandleType.Pinned);
        try
        {
            inBlob.cbData = input.Length;
            inBlob.pbData = pin.AddrOfPinnedObject();

            bool ok = encrypt
                ? CryptProtectData(ref inBlob, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CRYPTPROTECT_UI_FORBIDDEN, ref outBlob)
                : CryptUnprotectData(ref inBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CRYPTPROTECT_UI_FORBIDDEN, ref outBlob);
            if (!ok)
            {
                int err = Marshal.GetLastPInvokeError();
                throw new InvalidOperationException(
                    $"DPAPI {(encrypt ? "protect" : "unprotect")} failed (0x{err:X8})");
            }

            var result = new byte[outBlob.cbData];
            Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
            return result;
        }
        finally
        {
            if (pin.IsAllocated) pin.Free();
            if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DATA_BLOB pDataIn, string? szDataDescr,
        IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, uint dwFlags, ref DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DATA_BLOB pDataIn, IntPtr ppszDataDescr,
        IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, uint dwFlags, ref DATA_BLOB pDataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);
}

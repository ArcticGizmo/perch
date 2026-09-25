using System.Runtime.InteropServices;
using Perch.Platform;

namespace Perch.Platform.Windows;

/// <summary>
/// Windows <see cref="IMotionPreference"/>: the "Animation effects" toggle (Settings → Accessibility → Visual
/// effects), read with <c>SystemParametersInfo(SPI_GETCLIENTAREAANIMATION)</c> — the switch Windows itself
/// honours for in-app animations. Off means reduce motion. Any failure reads as "animate".
/// </summary>
public sealed class MotionPreference : IMotionPreference
{
    private const uint SPI_GETCLIENTAREAANIMATION = 0x1042;

    public bool ReduceMotion
    {
        get
        {
            try
            {
                int enabled = 1;
                return SystemParametersInfo(SPI_GETCLIENTAREAANIMATION, 0, ref enabled, 0) && enabled == 0;
            }
            catch { return false; }
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref int pvParam, uint fWinIni);
}

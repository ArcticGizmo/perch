using System.Media;
using Perch.Data;
using Perch.Platform;

namespace Perch.Platform.Windows;

/// <summary>
/// Windows <see cref="IAudioCue"/>: plays the built-in system chime matching a notification type.
/// Asterisk is the soft "information" tone (Done); Exclamation is the sharper "attention" tone
/// (WaitingForInput). Both are fire-and-forget and honour the user's per-event sound scheme in Windows.
/// (Moved from the WinForms app's NotificationService.)
/// </summary>
public sealed class AudioCue : IAudioCue
{
    public void Play(NotificationKind kind)
    {
        switch (kind)
        {
            case NotificationKind.Done:
                SystemSounds.Asterisk.Play();
                break;
            case NotificationKind.WaitingForInput:
                SystemSounds.Exclamation.Play();
                break;
        }
    }
}

using System.Diagnostics;
using Perch.Data;
using Perch.Platform;

namespace Perch.Platform.Mac;

/// <summary>
/// macOS <see cref="IAudioCue"/>: plays a built-in system sound matching the notification type by
/// shelling out to <c>/usr/bin/afplay</c> (ships with every macOS install) against the stock
/// <c>/System/Library/Sounds</c> aiffs. "Glass" is the soft completion tone (Done); "Funk" is the
/// sharper attention tone (WaitingForInput) — the macOS analogues of the Windows Asterisk/Exclamation
/// mapping. Fire-and-forget; never throws.
///
/// NOTE (Phase 3): written against documented paths but not yet verified on a Mac. The obvious future
/// refinement is <c>NSSound</c> via AppKit P/Invoke to avoid spawning a process per chime.
/// </summary>
public sealed class AudioCue : IAudioCue
{
    public void Play(NotificationKind kind)
    {
        string sound = kind switch
        {
            NotificationKind.Done => "Glass",
            NotificationKind.WaitingForInput => "Funk",
            _ => "",
        };
        if (sound.Length == 0) return;

        string path = $"/System/Library/Sounds/{sound}.aiff";
        try
        {
            if (!File.Exists(path)) return;
            var psi = new ProcessStartInfo("/usr/bin/afplay") { UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add(path);
            // Disposing the Process handle doesn't stop the child — afplay plays out and exits on its own.
            using var _ = Process.Start(psi);
        }
        catch { /* best-effort: a missing sound file or afplay just means no chime */ }
    }
}

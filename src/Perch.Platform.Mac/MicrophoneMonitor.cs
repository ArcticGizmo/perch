namespace Perch.Platform.Mac;

/// <summary>
/// macOS <see cref="IMicrophoneMonitor"/> — a stub for now, like the other unfinished members of this
/// project (see <c>docs/macos-port-plan.md</c>). It reports null, which the contract defines as "this
/// platform can't tell you", so the overlay's mic strip simply never appears on the mac head rather than
/// showing a permanently-idle microphone.
///
/// A real implementation would come from CoreAudio rather than anything Windows-shaped: the
/// <c>kAudioDevicePropertyDeviceIsRunningSomewhere</c> property on the default input device gives the
/// "something is capturing" half, and a property listener on it makes that genuinely event-driven instead
/// of polled. Attributing capture to a specific application is the harder half — macOS has no public
/// per-process capture attribution equivalent to the ConsentStore, so a first cut would likely name the
/// frontmost/known call app instead and leave <see cref="MicUser.ProcessId"/> pointing at it. Note the
/// Teams integration behind <see cref="ICallController"/> needs none of this: its local API is reachable
/// on macOS too, so meeting state and mute work there as soon as this head is wired up.
/// </summary>
public sealed class MicrophoneMonitor : IMicrophoneMonitor
{
    public MicSnapshot? Current => null;

    public event Action? Changed { add { } remove { } }

    public void Start() { }
    public void Stop() { }
    public void SetDeviceMuted(bool muted) { }
    public void Dispose() { }
}

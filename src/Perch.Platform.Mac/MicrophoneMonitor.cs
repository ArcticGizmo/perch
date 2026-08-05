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
/// frontmost/known call app instead and leave <see cref="MicUser.ProcessId"/> pointing at it — which, given the
/// strip's only action is "focus that app", would still be most of the value.
/// </summary>
public sealed class MicrophoneMonitor : IMicrophoneMonitor
{
    public MicSnapshot? Current => null;

    public event Action? Changed { add { } remove { } }

    public void Start() { }
    public void Stop() { }
    public void Dispose() { }
}

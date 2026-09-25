using Perch.Platform;

namespace Perch.Platform.Mac;

/// <summary>
/// macOS <see cref="IMotionPreference"/> — stub. The real source is
/// <c>NSWorkspace.sharedWorkspace.accessibilityDisplayShouldReduceMotion</c>, reachable via the objc runtime
/// like the rest of this project's AppKit calls; wiring it is a port item. Until then this reports "animate",
/// the macOS default, so pulses simply run as they do on a default Windows install.
/// </summary>
public sealed class MotionPreference : IMotionPreference
{
    public bool ReduceMotion => false;   // TODO(mac): accessibilityDisplayShouldReduceMotion
}

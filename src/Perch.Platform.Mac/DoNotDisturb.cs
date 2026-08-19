using Perch.Platform;

namespace Perch.Platform.Mac;

/// <summary>
/// macOS <see cref="IDoNotDisturb"/> — stub. macOS surfaces Focus/DND state through the (private)
/// <c>_DKDoNotDisturbEnabled</c> defaults and, on newer systems, the Focus/DoNotDisturb frameworks; wiring one
/// up safely is a Phase-3 item. Until then this reports "not in DND", so the friends region simply never
/// auto-collapses on macOS — a safe default that hides nothing.
/// </summary>
public sealed class MacDoNotDisturb : IDoNotDisturb
{
    public bool IsActive => false;   // TODO(mac): read Focus / Do Not Disturb state
}

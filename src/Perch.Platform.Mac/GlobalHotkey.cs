using Perch.Platform;

namespace Perch.Platform.Mac;

/// <summary>
/// macOS <see cref="IGlobalHotkey"/>. Phase 3: register via Carbon <c>RegisterEventHotKey</c> (fires
/// without focus) or an <c>NSEvent</c> global monitor (needs the Accessibility / Input-Monitoring
/// permission). Stub for now: <see cref="Register"/> reports refusal, which the caller safely ignores.
/// </summary>
public sealed class GlobalHotkey : IGlobalHotkey
{
    public bool Register(HotkeyModifiers modifiers, char key, Action onPressed) => false;
    public void Dispose() { }
}

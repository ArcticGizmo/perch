namespace Perch.Platform;

/// <summary>
/// A process-wide keyboard shortcut, registered with the OS so it fires even when Perch has no focused
/// window. On Windows it's implemented with <c>RegisterHotKey</c>/<c>WM_HOTKEY</c>; other platforms will
/// provide their own. Resolved by the app's composition root so no UI code hard-codes the interop.
/// Dispose to unregister and release the OS binding.
/// </summary>
public interface IGlobalHotkey : IDisposable
{
    /// <summary>
    /// Registers <paramref name="modifiers"/> + <paramref name="key"/> (a letter or digit) and invokes
    /// <paramref name="onPressed"/> whenever the combo fires. The callback runs on an arbitrary thread —
    /// the caller marshals to its UI thread. Returns false if the OS refused the binding (e.g. another
    /// app already owns the combo); a refusal is safe to ignore. Call once per instance.
    /// </summary>
    bool Register(HotkeyModifiers modifiers, char key, Action onPressed);
}

/// <summary>The modifier keys that must be held for a <see cref="IGlobalHotkey"/> to fire.</summary>
[Flags]
public enum HotkeyModifiers
{
    None    = 0,
    Alt     = 1,
    Control = 2,
    Shift   = 4,
}

using System.Collections.Concurrent;

namespace Perch.Data.Control;

/// <summary>
/// The session ids this Perch instance currently owns over stream-json (<see cref="ClaudeSessionController"/>
/// registers on init, unregisters on exit). Consulted so the other control surfaces don't double-handle
/// an owned session — the permission valet must pass (the console already answers <c>can_use_tool</c>
/// over stdin), and focus routing should target the console window rather than hunt for a terminal.
/// </summary>
internal static class ControlledSessions
{
    private static readonly ConcurrentDictionary<string, byte> Ids = new();

    public static void Register(string sessionId)
    {
        if (!string.IsNullOrEmpty(sessionId)) Ids[sessionId] = 1;
    }

    public static void Unregister(string? sessionId)
    {
        if (!string.IsNullOrEmpty(sessionId)) Ids.TryRemove(sessionId, out _);
    }

    public static bool Owns(string? sessionId) =>
        !string.IsNullOrEmpty(sessionId) && Ids.ContainsKey(sessionId);
}

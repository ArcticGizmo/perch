namespace Perch.Data.Control;

/// <summary>
/// Decides when a Perch-controlled session should raise a desktop attention cue: a permission (or a question /
/// plan approval — anything that pauses the turn waiting on the user) became pending while the session's
/// window is <em>not</em> the active one, so the user, working elsewhere, would otherwise miss it.
///
/// Edge-triggered and stateful so it fires at most once per pending item: feed it the current
/// <see cref="PermissionItem"/> (or null) and whether the window is active on every state or activation change,
/// and it returns true exactly on the transition into "pending &amp; background" for a given item. A pending
/// item that was raised while the window was active still alerts if the user later tabs away from it; once the
/// item resolves (null) the latch clears, so the next permission alerts afresh. UI-free and unit-tested; the
/// window owns one and routes a true result to <c>INotifier</c>.
/// </summary>
internal sealed class SessionAttention
{
    private PermissionItem? _alertedFor;

    /// <summary>Returns true exactly once when <paramref name="pending"/> first becomes pending while
    /// <paramref name="windowActive"/> is false. No alert while the window is active or nothing is pending.</summary>
    public bool Evaluate(PermissionItem? pending, bool windowActive)
    {
        if (pending is null)
        {
            _alertedFor = null;   // resolved — re-arm for the next permission
            return false;
        }
        if (windowActive) return false;                        // the user is looking at it — no cue needed
        if (ReferenceEquals(_alertedFor, pending)) return false; // already alerted for this one
        _alertedFor = pending;
        return true;
    }
}

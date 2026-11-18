namespace Perch.Data.Sprig;

/// <summary>One notification about a sprig workspace. <see cref="Problem"/> marks the ones that are bad news.</summary>
internal sealed record SprigNotice(string Title, string Body, string WorkspaceKey, bool Problem);

/// <summary>
/// What's worth a notification between one <see cref="SprigStatus"/> and the next: an app that has just exited or
/// failed, a workspace that has just failed to start, and a workspace started from Perch becoming ready.
/// </summary>
/// <remarks>
/// <para>Only changes count. Nothing is said until both sides have seen a snapshot, so turning the integration on,
/// or Perch starting, never replays failures that were already there. An app sprig stops on request goes
/// <c>stopping</c> → <c>stopped</c>, and one whose host has gone reads <c>off</c>, so neither reads as a failure.</para>
/// <para>"Ready" is only for workspaces in <c>awaitingReady</c> (the ones the user started, restarted or resumed from
/// Perch). Telling people about every workspace that comes up, including ones they started in a terminal and are
/// watching, would be noise. A key leaves the set when it's ready, fails, is removed, or is stopped or paused after
/// it had got going.</para>
/// </remarks>
internal static class SprigNotices
{
    public static IReadOnlyList<SprigNotice> Diff(SprigStatus before, SprigStatus after, ISet<string> awaitingReady)
    {
        var notices = new List<SprigNotice>();
        foreach (var key in awaitingReady.Where(k => after.Seen && after.Find(k) is null).ToArray())
            awaitingReady.Remove(key);
        if (!before.Seen || !after.Seen) return notices;

        foreach (var w in after.Workspaces)
        {
            if (w.IsFree || before.Find(w.Key) is not { } was) continue;

            if (w.State == SprigWorkspaceState.Failed)
            {
                awaitingReady.Remove(w.Key);
                if (was.State != SprigWorkspaceState.Failed)
                    notices.Add(new($"{w.Label} failed to start", w.Error ?? FailedApps(w) ?? "Open sprig for the details.", w.Key, true));
                continue;
            }

            var newlyFailed = w.Apps
                .Where(a => a.IsFailed && was.Apps.FirstOrDefault(p => p.Name == a.Name) is { IsFailed: false })
                .ToArray();
            if (newlyFailed.Length == 1)
            {
                var a = newlyFailed[0];
                notices.Add(new($"{a.ShortName} {(a.State == "failed" ? "failed" : "exited")} · {w.Label}", AppReason(a), w.Key, true));
            }
            else if (newlyFailed.Length > 1)
            {
                notices.Add(new($"{newlyFailed.Length} apps stopped working · {w.Label}",
                    string.Join(", ", newlyFailed.Select(a => $"{a.ShortName} ({AppReason(a)})")), w.Key, true));
            }
            if (newlyFailed.Length > 0) awaitingReady.Remove(w.Key);

            bool parked = w.State is SprigWorkspaceState.Stopped or SprigWorkspaceState.Paused;
            bool wasParked = was.State is SprigWorkspaceState.Stopped or SprigWorkspaceState.Paused;
            if (parked && !wasParked) awaitingReady.Remove(w.Key);

            if (awaitingReady.Contains(w.Key) && w.State == SprigWorkspaceState.Running
                && w.ActiveCount > 0 && w.ReadyCount == w.ActiveCount && !w.AnyFailed)
            {
                awaitingReady.Remove(w.Key);
                var ports = w.Apps.Where(a => a is { IsReady: true, Port: not null }).Select(a => $"{a.ShortName} :{a.Port}").ToArray();
                var body = ports.Length > 0 ? string.Join(" · ", ports)
                    : w.ReadyCount == 1 ? "1 app is ready." : $"{w.ReadyCount} apps are ready.";
                notices.Add(new($"{w.Label} is ready", body, w.Key, false));
            }
        }
        return notices;
    }

    private static string AppReason(SprigApp a)
        => a.Error is { Length: > 0 } e ? e : a.Exit is { } code ? $"exit code {code}" : a.State;

    private static string? FailedApps(SprigWorkspace w)
        => w.Apps.Where(a => a.IsFailed).Select(a => $"{a.ShortName}: {AppReason(a)}").ToArray() is { Length: > 0 } list
            ? string.Join("; ", list) : null;
}

using Perch.Avalonia.Views;
using Perch.Data;

namespace Perch.Avalonia.Services;

/// <summary>
/// The single place that maps a persisted <see cref="AppSettings"/> onto an <see cref="OverlayCanvas"/>'s
/// display gates. Pure UI wiring — it only calls the canvas's <c>Set*</c> methods and touches nothing in
/// the data layer — so it's safe to run against any canvas, including a detached preview one seeded with a
/// temporary settings snapshot.
///
/// <para>The live overlay drives this from <c>App.ApplyDisplaySettings</c> (which additionally flips the
/// monitor's data-layer sources); the Settings live-preview pane drives the same method against its own
/// canvas and a cloned <see cref="AppSettings"/>, so the two can never drift.</para>
/// </summary>
internal static class OverlaySettingsGates
{
    /// <summary>Pushes every overlay display gate from <paramref name="s"/> onto <paramref name="c"/>.</summary>
    public static void Apply(OverlayCanvas c, AppSettings s)
    {
        c.SetShowUsage(s.ShowUsage);
        c.SetShowExpectedRate(s.ShowExpectedUsageRate);
        c.SetShowSystemMetrics(s.ShowSystemMetrics);
        c.SetShowSessionMetrics(s.ShowSessionMetrics);
        c.SetShowContextPressure(s.ShowContextPressure);
        c.SetShowContextGreenSegment(s.ShowContextGreenSegment);
        c.SetContextThresholds(s.ContextPressureYellowPercent, s.ContextPressureOrangePercent, s.ContextPressureRedPercent);
        c.SetShowModeBadges(s.ShowPermissionModeBadges);
        c.SetShowTaskProgress(s.ShowTaskProgress);
        c.SetShowNoteLine(s.ShowNotes);
        c.SetShowBurnRate(s.ShowBurnRate);
        c.SetShowGitStats(s.ShowGitStats);
        c.SetShowPullRequests(s.ShowPullRequests);
        c.SetShowDaemonProcesses(s.ShowDaemonProcesses);
        c.SetStuckDetectionEnabled(s.StuckDetectionEnabled);
        c.SetShowWaitingTimer(s.ShowWaitingTimer);
        c.SetWaitingTimerRedMinutes(s.WaitingTimerRedMinutes);
        c.SetShowArtifacts(s.ShowArtifacts);
        c.SetServiceStatusEnabled(s.ShowServiceStatus);
        c.SetShowMediaController(s.ShowMediaController);
        c.SetShowMicPresence(s.ShowMicPresence);
        c.SetHideInactiveTeamMembers(s.HideInactiveTeamMembers);
        c.SetUpsideDownQuickLinks(s.UpsideDownQuickLinks);
        c.SetConfettiFinishAvailable(s.ConfettiFinish);
        c.SetExternalNotificationsAvailable(s.ExternalNotificationsEnabled);
        c.SetReviewChangesAvailable(s.ShowGitReview);
    }
}

using Avalonia.Threading;
using Perch.Avalonia.Views;
using Perch.Data;
using Perch.Platform;
using Perch.Plugins;

namespace Perch.Avalonia.Services;

/// <summary>
/// Runs the installed, enabled, consented plugins on a fixed cadence and feeds the overlay's Plugins
/// section. A <see cref="DispatcherTimer"/> ticks on the UI thread; each tick discovers plugins on disk,
/// resolves the runnable set (<see cref="PluginHost"/> — master switch × consent), polls each
/// out-of-process (the launch/stdio is async so it runs off the UI thread), then — back on the UI thread —
/// pushes the resulting glyphs to the canvas, raises any permitted notifications, and applies the fault
/// policy (auto-disabling a plugin that keeps failing, persisting that change).
///
/// Everything a plugin can do is already enforced upstream (<see cref="PluginService"/> drops actions the
/// grant forbids); this host is the scheduler and the bridge to the UI. It never throws for plugin
/// misbehaviour.
/// </summary>
internal sealed class PluginMonitorHost : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    private readonly Action<IReadOnlyList<OverlayCanvas.PluginBadge>> _onBadges;
    private readonly NotificationService _notifications;
    private readonly AppSettings _settings;
    private readonly Func<PluginPollContext> _context;
    private readonly PluginService _service = new(PlatformServices.CreatePluginSandbox(), AppInfo.Version);
    private readonly DispatcherTimer _timer;

    private bool _disposed;
    private bool _polling;

    /// <param name="onBadges">Pushes the current plugin glyphs to the overlay (called on the UI thread).</param>
    /// <param name="contextProvider">Supplies the session context (cwd/id) offered to plugins; only the
    /// parts a plugin's grant permits are actually sent. Defaults to empty.</param>
    public PluginMonitorHost(
        Action<IReadOnlyList<OverlayCanvas.PluginBadge>> onBadges,
        NotificationService notifications,
        AppSettings settings,
        Func<PluginPollContext>? contextProvider = null)
    {
        _onBadges = onBadges;
        _notifications = notifications;
        _settings = settings;
        _context = contextProvider ?? (() => PluginPollContext.Empty);
        _timer = new DispatcherTimer { Interval = Interval };
        _timer.Tick += (_, _) => _ = PollOnce();
    }

    /// <summary>Starts the timer and runs the first pass now. Call on the UI thread.</summary>
    public void Start()
    {
        if (_disposed) return;
        _timer.Start();
        _ = PollOnce();
    }

    public void Stop()
    {
        _timer.Stop();
        // Clear the section so a disabled subsystem leaves nothing painted.
        _onBadges([]);
    }

    /// <summary>Re-scan and poll immediately (after an install / enable / disable / master-switch change).</summary>
    public void Refresh() => _ = PollOnce();

    private async Task PollOnce()
    {
        if (_disposed || _polling) return;
        _polling = true;
        try
        {
            var discovered = new PluginRegistry(ClaudePaths.PerchPluginsDir, AppInfo.Version).Discover();
            var records = _settings.InstalledPlugins ?? [];
            var runnable = PluginHost.Resolve(_settings.PluginsEnabled, discovered, records);

            if (runnable.Count == 0)
            {
                if (!_disposed) _onBadges([]);
                return;
            }

            var ctx = _context();
            var badges = new List<OverlayCanvas.PluginBadge>();
            bool recordsChanged = false;

            foreach (var r in runnable)
            {
                PluginPollResult result;
                try { result = await _service.PollAsync(r.Discovered, ctx, r.Grants); }
                catch { continue; }
                if (_disposed) return;

                if (PluginHealth.RecordResult(r.Record, result))
                {
                    recordsChanged = true;
                    _notifications.ShowInfo("Plugin disabled",
                        $"\"{r.Discovered.Manifest!.Name}\" was disabled after repeated failures.", ToastLevel.Warning);
                }

                foreach (var n in result.Notifications)
                    _notifications.ShowInfo(n.Title, n.Body, ToastLevel.Info);

                if (result.Glyph is { } g)
                    badges.Add(new OverlayCanvas.PluginBadge(
                        r.Record.Id, r.Discovered.Manifest!.Name, g.Glyph, g.Text, g.Tooltip, result.Ok));
            }

            if (_disposed) return;
            if (recordsChanged) _settings.Save();
            _onBadges(badges);
        }
        catch { /* best-effort: a bad poll never takes down the tray */ }
        finally { _polling = false; }
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Stop();
    }
}

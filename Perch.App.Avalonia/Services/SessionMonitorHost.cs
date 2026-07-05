using Avalonia.Threading;
using Perch.Avalonia.ViewModels;
using Perch.Data;

namespace Perch.Avalonia.Services;

/// <summary>
/// Owns the Perch.Core <see cref="SessionMonitor"/> for the Avalonia app and pumps its results into the
/// <see cref="OverlayViewModel"/>. Mirrors the WinForms context's contract: every file-change trigger
/// is marshalled onto the UI thread before <see cref="SessionMonitor.Scan"/> runs, so
/// <see cref="SessionMonitor.SessionsChanged"/> (and the view-model update it drives) always fire on
/// the UI thread. This is the pipeline the whole Avalonia UI hangs off, so it's the thing the thin
/// vertical proves end-to-end.
/// </summary>
internal sealed class SessionMonitorHost : IDisposable
{
    private readonly SessionMonitor _monitor = new();
    private readonly OverlayViewModel _vm;

    public SessionMonitorHost(OverlayViewModel vm)
    {
        _vm = vm;
        _monitor.SessionsChanged += OnSessionsChanged;
        // FileSystemWatcher/debounce fire on background threads; hop to the UI thread and re-scan there
        // (matches the WinForms BeginInvoke(Scan) pattern) so the view-model is only touched on the UI thread.
        _monitor.ChangeDetected += () => Dispatcher.UIThread.Post(() => _monitor.Scan());
    }

    /// <summary>Reads the initial session state. Call on the UI thread (Scan raises SessionsChanged).</summary>
    public void Start() => _monitor.Scan();

    private void OnSessionsChanged(IReadOnlyList<ClaudeSession> sessions) => _vm.Update(sessions);

    public void Dispose()
    {
        _monitor.SessionsChanged -= OnSessionsChanged;
        _monitor.Dispose();
    }
}

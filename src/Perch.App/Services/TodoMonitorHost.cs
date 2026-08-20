using Avalonia.Threading;
using Perch.Avalonia.Views;
using Perch.Data;
using Perch.Platform;

namespace Perch.Avalonia.Services;

/// <summary>
/// Keeps the overlay's "To do" strip current and fires the due reminders. A <see cref="DispatcherTimer"/>
/// ticks on the UI thread every minute; each tick recomputes the top few outstanding items and pushes them
/// to the canvas, then (when reminders are enabled) toasts any item whose due time has passed and hasn't
/// yet been announced — stamping <see cref="Todo.ReminderFiredUtc"/> and saving so it nags exactly once,
/// even across restarts.
///
/// <para>Holds the single shared <see cref="TodoStore"/> instance the Todos window edits, so a change made
/// there is reflected on the next tick (or immediately, via <see cref="RefreshNow"/>) without reloading
/// from disk. The store's file IO is trivial and local, so it runs inline on the tick rather than off it.</para>
/// </summary>
internal sealed class TodoMonitorHost : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    // At most this many lines on the overlay strip — matches OverlayCanvas.MaxTodoRows.
    private const int StripCount = 3;

    private readonly TodoStore _store;
    private readonly Action<IReadOnlyList<OverlayCanvas.TodoLine>, int> _onTodos;
    private readonly NotificationService _notifications;
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _timer;

    public TodoMonitorHost(TodoStore store, Action<IReadOnlyList<OverlayCanvas.TodoLine>, int> onTodos,
        NotificationService notifications, AppSettings settings)
    {
        _store = store;
        _onTodos = onTodos;
        _notifications = notifications;
        _settings = settings;
        _timer = new DispatcherTimer { Interval = Interval };
        _timer.Tick += (_, _) => Tick();
    }

    /// <summary>Starts the timer and runs the first pass now. Call on the UI thread.</summary>
    public void Start()
    {
        _timer.Start();
        Tick();
    }

    /// <summary>Stops polling and clears the strip (the feature was turned off).</summary>
    public void Stop()
    {
        _timer.Stop();
        _onTodos([], 0);
    }

    /// <summary>Re-runs the feed + reminder pass immediately — used after the overlay completes an item, so
    /// the strip drops it without waiting for the next minute.</summary>
    public void RefreshNow() => Tick();

    private void Tick()
    {
        var now = DateTime.UtcNow;
        FireDueReminders(now);
        Feed(now);
    }

    private void Feed(DateTime now)
    {
        var lines = _store.TopOutstanding(StripCount)
            .Select(t => new OverlayCanvas.TodoLine(
                t.Id,
                t.Title.Length > 0 ? t.Title : "(untitled)",
                t.DueUtc is { } due ? RelativeTime.DueLabel(now, due) : null,
                Overdue: t.DueUtc is { } d && d < now))
            .ToList();
        int outstanding = _store.All().Count(t => !t.Completed);
        _onTodos(lines, outstanding);
    }

    private void FireDueReminders(DateTime now)
    {
        if (!_settings.TodoRemindersEnabled) return;

        bool any = false;
        foreach (var t in TodoStore.DueForReminder(_store.All(), now).ToList())
        {
            var title = t.Title.Length > 0 ? t.Title : "(untitled)";
            _notifications.ShowInfo("Perch reminder", $"\"{title}\" is due", ToastLevel.Warning);
            t.ReminderFiredUtc = now;
            any = true;
        }
        if (any) _store.Save();
    }

    public void Dispose() => _timer.Stop();
}

namespace Perch.Ui;

/// <summary>
/// Helpers for the "do work off the UI thread, then apply the result back on it" pattern that the
/// overlay context and the stats/history windows each repeated by hand: <c>Task.Run</c> →
/// <c>ContinueWith</c> → a guarded <c>BeginInvoke</c> that swallows the benign
/// <see cref="ObjectDisposedException"/> / <see cref="InvalidOperationException"/> races which occur
/// when a window closes mid-flight. (See CLAUDE.md: "IO / heavy work runs off the UI thread, then
/// marshals back".)
/// </summary>
internal static class UiDispatch
{
    /// <summary>
    /// Marshals <paramref name="action"/> onto <paramref name="control"/>'s UI thread, best-effort:
    /// it is skipped if the control's handle is gone, and the disposed-mid-post races are swallowed.
    /// </summary>
    public static void Post(Control control, Action action)
    {
        try
        {
            if (control.IsHandleCreated && !control.IsDisposed)
                control.BeginInvoke(action);
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    /// <summary>
    /// Runs <paramref name="work"/> on the thread pool, then posts <paramref name="apply"/> with its
    /// result back onto <paramref name="control"/>'s UI thread. If <paramref name="work"/> faults or is
    /// cancelled, <paramref name="fallback"/> is applied instead, so a caller always gets a result.
    /// </summary>
    public static void RunThenPost<T>(Control control, Func<T> work, Action<T> apply, T fallback)
    {
        Task.Run(work).ContinueWith(t =>
        {
            var result = t.IsCompletedSuccessfully ? t.Result : fallback;
            Post(control, () => apply(result));
        });
    }
}

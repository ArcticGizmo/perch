using System.Runtime.InteropServices;
using Perch.Data;

namespace Perch.Platform.Windows;

/// <summary>
/// Steers a Windows Terminal window to a session's own tab. Win32 can only raise the terminal
/// <em>window</em>, which then shows whatever tab was last active; WT's tabs are UIA <c>TabItem</c>
/// elements supporting <c>SelectionItemPattern</c>, so UI Automation can pick the right one. Which tab is the
/// session's is decided by <see cref="TerminalTabMatch"/> from the session's console title (read through
/// <see cref="ConsoleAttach"/>), which is also WT's name for that tab.
/// <para>
/// Talks to the OS's own <c>CUIAutomation</c> COM object through the minimal <c>[ComImport]</c> surface
/// below, rather than pulling in WPF's managed <c>UIAutomationClient</c>. Best-effort throughout: any failure
/// leaves the tab as it was, and the caller raises the window regardless.
/// </para>
/// </summary>
internal sealed class WindowsTerminalTabs
{
    // UI Automation calls block until Windows Terminal answers, and a focus request comes from a click, i.e. the
    // UI thread: a hung WT would freeze the overlay for UIA's own timeout (tens of seconds). So the walk runs on
    // the thread pool (MTA, which UIA clients prefer anyway) and the click waits at most SelectWait for it.
    // That is ample for the usual ~150 ms walk, so the tab is normally selected before the window comes
    // forward; when WT is slower the window is raised regardless and the tab follows if it arrives within
    // SelectLateMs, or not at all, since switching tabs long after the click would be a surprise rather than a
    // response. Only one walk runs at a time, so clicks on a hung WT don't stack threads.
    private static readonly TimeSpan SelectWait = TimeSpan.FromMilliseconds(500);
    private const int SelectLateMs = 2000;
    private static int _busy;

    // Several tabs carry the session's title (typically a few untitled "Claude Code" sessions), so the title is
    // tagged for a moment: WT renames exactly the session's tab, the marker is unique, and that tab is the one.
    // This is the classic way to find a console's window from its title. The original title is put back
    // whatever happens. No marker turns up when the tab ignores app titles (renamed, or a profile with
    // suppressApplicationTitle), or when Claude Code redraws its title before WT shows ours.
    private const int MarkerWaitMs = 400;
    private const int MarkerPollMs = 25;

    /// <summary>
    /// Selects <paramref name="pid"/>'s tab in <paramref name="terminalWindow"/>. Returns a <see cref="TabMiss"/>
    /// when the window has several tabs and none could be told to be the session's, so the caller can say so;
    /// null otherwise, including when the answer took too long to matter.
    /// </summary>
    public static TabMiss? SelectSessionTab(IntPtr terminalWindow, int pid)
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1)
            return null;

        long selectBy = Environment.TickCount64 + SelectLateMs;
        var walk = Task.Run(() =>
        {
            try
            {
                return SelectSessionTabCore(terminalWindow, pid, selectBy);
            }
            catch
            {
                return null;
            }
            finally
            {
                Volatile.Write(ref _busy, 0);
            }
        });
        return walk.Wait(SelectWait) ? walk.Result : null;
    }

    private static TabMiss? SelectSessionTabCore(IntPtr terminalWindow, int pid, long selectBy)
    {
        var title = ConsoleAttach.With(pid, () => ConsoleAttach.Title());
        if (string.IsNullOrEmpty(title)) return null;

        var tabs = Open(terminalWindow);
        if (tabs is null) return null;

        var match = TerminalTabMatch.Match(tabs.ReadNames(), title);
        switch (match.Kind)
        {
            case TabMatchKind.Found:
                if (Environment.TickCount64 <= selectBy) tabs.Select(match.Index);
                return null;
            case TabMatchKind.None:
                // A lone tab is already the one on show, so there is nothing to apologise for.
                return tabs.Count > 1 ? new TabMiss(TabMissReason.NotFound, title, 0) : null;
            default:
                return tabs.SelectByMarker(pid, title, selectBy)
                    ? null
                    : new TabMiss(TabMissReason.Ambiguous, title, match.Count);
        }
    }

    private bool SelectByMarker(int pid, string title, long selectBy)
    {
        var marked = TerminalTabMatch.Mark(title, Guid.NewGuid().ToString("N")[..4]);
        if (!ConsoleAttach.With(pid, () => ConsoleAttach.SetTitle(marked))) return false;
        try
        {
            long giveUp = Math.Min(selectBy, Environment.TickCount64 + MarkerWaitMs);
            while (Environment.TickCount64 < giveUp)
            {
                Thread.Sleep(MarkerPollMs);
                int index = TerminalTabMatch.FindMarked(ReadNames(), marked);
                if (index >= 0) return Select(index);
            }
            return false;
        }
        finally
        {
            // Only while the title is still ours: the session may have set a newer one meanwhile (Claude Code
            // redraws its title as it works), and that one must stand.
            ConsoleAttach.With(pid, () =>
                ConsoleAttach.Title(marked.Length + 2) == marked && ConsoleAttach.SetTitle(title));
        }
    }

    // ── One window's tabs ────────────────────────────────────────────────────
    // The tab elements are found once and kept, because ReadNames is called repeatedly while waiting for a
    // marker to show up, and re-reading a name is far cheaper than another tree walk.

    private const int UIA_ControlTypePropertyId = 30003;
    private const int UIA_TabItemControlTypeId = 50019;
    private const int UIA_SelectionItemPatternId = 10010;
    private const int TreeScope_Descendants = 0x4;

    private static readonly Guid CLSID_CUIAutomation = new("ff48dba4-60ef-4201-aa87-54103eef594e");

    private readonly List<IUIAutomationElement> _tabs;

    private WindowsTerminalTabs(List<IUIAutomationElement> tabs) => _tabs = tabs;

    private int Count => _tabs.Count;

    // The tabs of terminalWindow, or null when it has none or UIA fails. The automation object is only needed
    // to find them; the elements keep their own references.
    private static WindowsTerminalTabs? Open(IntPtr terminalWindow)
    {
        IUIAutomation? uia = null;
        try
        {
            var type = Type.GetTypeFromCLSID(CLSID_CUIAutomation);
            if (type is null) return null;
            uia = Activator.CreateInstance(type) as IUIAutomation;
            if (uia is null) return null;

            if (uia.ElementFromHandle(terminalWindow, out var window) != 0 || window is null) return null;
            if (uia.CreatePropertyCondition(UIA_ControlTypePropertyId, UIA_TabItemControlTypeId, out var isTab) != 0
                || isTab is null) return null;
            if (window.FindAll(TreeScope_Descendants, isTab, out var found) != 0 || found is null) return null;
            if (found.get_Length(out int count) != 0 || count == 0) return null;

            var tabs = new List<IUIAutomationElement>(count);
            for (int i = 0; i < count; i++)
                if (found.GetElement(i, out var tab) == 0 && tab is not null)
                    tabs.Add(tab);
            return tabs.Count == 0 ? null : new WindowsTerminalTabs(tabs);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (uia is not null) Marshal.FinalReleaseComObject(uia);
        }
    }

    // Each tab's current name, in tab order; "" for one that can't be read.
    private List<string> ReadNames()
    {
        var names = new List<string>(_tabs.Count);
        foreach (var tab in _tabs)
        {
            string? name = null;
            try { tab.get_CurrentName(out name); }
            catch { /* a tab closed mid-read */ }
            names.Add(name ?? "");
        }
        return names;
    }

    // Selects tab index; true when it is now the selected tab.
    private bool Select(int index)
    {
        try
        {
            if (index < 0 || index >= _tabs.Count) return false;
            if (_tabs[index].GetCurrentPattern(UIA_SelectionItemPatternId, out var pattern) != 0) return false;
            if (pattern is not IUIAutomationSelectionItemPattern selection) return false;

            // Selecting the tab that is already selected changes nothing on screen but leaves keyboard focus
            // on the tab strip, so the click would take the user's typing away from the session.
            if (selection.get_CurrentIsSelected(out int isSelected) == 0 && isSelected != 0) return true;
            return selection.Select() == 0;
        }
        catch
        {
            return false;
        }
    }

    // ── UI Automation COM surface ────────────────────────────────────────────
    // Classic [ComImport] interfaces must list every vtable slot up to the last one used, in the order of
    // UIAutomationClient.h; the unused ones are placeholders (never called, so their signatures don't matter).

    [ComImport, Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomation
    {
        void CompareElements();
        void CompareRuntimeIds();
        void GetRootElement();

        [PreserveSig]
        int ElementFromHandle(IntPtr hwnd, out IUIAutomationElement? element);

        void ElementFromPoint();
        void GetFocusedElement();
        void GetRootElementBuildCache();
        void ElementFromHandleBuildCache();
        void ElementFromPointBuildCache();
        void GetFocusedElementBuildCache();
        void CreateTreeWalker();
        void get_ControlViewWalker();
        void get_ContentViewWalker();
        void get_RawViewWalker();
        void get_RawViewCondition();
        void get_ControlViewCondition();
        void get_ContentViewCondition();
        void CreateCacheRequest();
        void CreateTrueCondition();
        void CreateFalseCondition();

        [PreserveSig]
        int CreatePropertyCondition(int propertyId, [MarshalAs(UnmanagedType.Struct)] object value,
            out IUIAutomationCondition? condition);
    }

    [ComImport, Guid("352ffba8-0973-437c-a61f-f64cafd81df9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationCondition { }

    [ComImport, Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationElement
    {
        void SetFocus();
        void GetRuntimeId();
        void FindFirst();

        [PreserveSig]
        int FindAll(int scope, IUIAutomationCondition condition, out IUIAutomationElementArray? found);

        void FindFirstBuildCache();
        void FindAllBuildCache();
        void BuildUpdatedCache();
        void GetCurrentPropertyValue();
        void GetCurrentPropertyValueEx();
        void GetCachedPropertyValue();
        void GetCachedPropertyValueEx();
        void GetCurrentPatternAs();
        void GetCachedPatternAs();

        [PreserveSig]
        int GetCurrentPattern(int patternId, [MarshalAs(UnmanagedType.IUnknown)] out object? pattern);

        void GetCachedPattern();
        void GetCachedParent();
        void GetCachedChildren();
        void get_CurrentProcessId();
        void get_CurrentControlType();
        void get_CurrentLocalizedControlType();

        [PreserveSig]
        int get_CurrentName([MarshalAs(UnmanagedType.BStr)] out string? name);
    }

    [ComImport, Guid("14314595-b4bc-4055-95f2-58f2e42c9855"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationElementArray
    {
        [PreserveSig]
        int get_Length(out int length);

        [PreserveSig]
        int GetElement(int index, out IUIAutomationElement? element);
    }

    [ComImport, Guid("a8efa66a-0fda-421a-9194-38021f3578ea"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationSelectionItemPattern
    {
        [PreserveSig]
        int Select();

        void AddToSelection();
        void RemoveFromSelection();

        [PreserveSig]
        int get_CurrentIsSelected(out int isSelected);
    }
}

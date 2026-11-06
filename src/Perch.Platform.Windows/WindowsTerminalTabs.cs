using System.Runtime.InteropServices;
using Perch.Data;

namespace Perch.Platform.Windows;

/// <summary>
/// The tabs of one Windows Terminal window, through UI Automation. Win32 can only raise the terminal
/// <em>window</em>, which then shows whatever tab was last active; WT's tabs are UIA <c>TabItem</c>
/// elements supporting <c>SelectionItemPattern</c>, so UIA can pick the right one. Which tab is the
/// session's is decided by <see cref="TerminalTabMatch"/>; this only reads names and selects.
/// <para>
/// The tab elements are found once and kept, because <see cref="ReadNames"/> may be called repeatedly while
/// waiting for a marked title to show up, and re-reading a name is far cheaper than another tree walk.
/// </para>
/// <para>
/// Talks to the OS's own <c>CUIAutomation</c> COM object through the minimal <c>[ComImport]</c> surface
/// below, rather than pulling in WPF's managed <c>UIAutomationClient</c>. Every member is best-effort and
/// never throws.
/// </para>
/// </summary>
internal sealed class WindowsTerminalTabs : IDisposable
{
    private const int UIA_ControlTypePropertyId = 30003;
    private const int UIA_TabItemControlTypeId = 50019;
    private const int UIA_SelectionItemPatternId = 10010;
    private const int TreeScope_Descendants = 0x4;

    private static readonly Guid CLSID_CUIAutomation = new("ff48dba4-60ef-4201-aa87-54103eef594e");

    private readonly IUIAutomation _uia;
    private readonly List<IUIAutomationElement> _tabs;

    private WindowsTerminalTabs(IUIAutomation uia, List<IUIAutomationElement> tabs)
    {
        _uia = uia;
        _tabs = tabs;
    }

    public int Count => _tabs.Count;

    /// <summary>The tabs of <paramref name="terminalWindow"/>, or null when it has none or UIA fails.</summary>
    public static WindowsTerminalTabs? Open(IntPtr terminalWindow)
    {
        IUIAutomation? uia = null;
        try
        {
            var type = Type.GetTypeFromCLSID(CLSID_CUIAutomation);
            if (type is null) return null;
            uia = Activator.CreateInstance(type) as IUIAutomation;
            if (uia is null) return null;

            if (uia.ElementFromHandle(terminalWindow, out var window) != 0 || window is null) return Fail(uia);
            if (uia.CreatePropertyCondition(UIA_ControlTypePropertyId, UIA_TabItemControlTypeId, out var isTab) != 0
                || isTab is null) return Fail(uia);
            if (window.FindAll(TreeScope_Descendants, isTab, out var found) != 0 || found is null) return Fail(uia);
            if (found.get_Length(out int count) != 0 || count == 0) return Fail(uia);

            var tabs = new List<IUIAutomationElement>(count);
            for (int i = 0; i < count; i++)
                if (found.GetElement(i, out var tab) == 0 && tab is not null)
                    tabs.Add(tab);
            return tabs.Count == 0 ? Fail(uia) : new WindowsTerminalTabs(uia, tabs);
        }
        catch
        {
            return uia is null ? null : Fail(uia);
        }

        static WindowsTerminalTabs? Fail(IUIAutomation uia)
        {
            Marshal.FinalReleaseComObject(uia);
            return null;
        }
    }

    /// <summary>Each tab's current name, in tab order; "" for one that can't be read.</summary>
    public List<string> ReadNames()
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

    /// <summary>Selects tab <paramref name="index"/>; true when WT accepted it.</summary>
    public bool Select(int index)
    {
        try
        {
            if (index < 0 || index >= _tabs.Count) return false;
            if (_tabs[index].GetCurrentPattern(UIA_SelectionItemPatternId, out var pattern) != 0) return false;
            return pattern is IUIAutomationSelectionItemPattern selection && selection.Select() == 0;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        try { Marshal.FinalReleaseComObject(_uia); }
        catch { /* already released */ }
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
    }
}

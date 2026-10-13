using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;

namespace Perch.Avalonia.Views;

/// <summary>
/// Makes a file reference actionable: a right-click context menu — <em>View</em> (Markdown only),
/// <em>View diff</em>, <em>Reveal in Explorer</em> (Finder on macOS), <em>Open in VS Code</em>, <em>Copy path</em>
/// — plus a left-click primary for a whole-control reference. <em>View</em>/<em>View diff</em> route out via the
/// caller's callbacks (the app owns the viewer/tree windows); reveal/editor go straight to
/// <see cref="PlatformServices.FileRevealer"/>, the same direct-to-platform shape <see cref="LinkText"/> uses
/// for URLs.
///
/// <para>Two shapes: <see cref="Attach"/> arms a whole control (a tool card's filename, a changed-files row);
/// <see cref="AttachInline"/> arms character ranges inside a <see cref="SelectableTextBlock"/> (file paths in
/// Claude's prose), following <see cref="LinkText"/>'s <em>Ctrl+click</em> convention so the surrounding text
/// stays selectable.</para>
///
/// <para>Opening never hands the file to the OS shell to run: a Markdown file opens in the in-app viewer, anything
/// else in VS Code (through <c>CmdShim</c>), whose fallback is the view-only default handler or a reveal (review
/// fixes CP8/CP12). Which files get here at all is <c>FileRefResolver</c>'s call (local, existing, never probed
/// over the network — CP9).</para>
/// </summary>
internal static class FileRef
{
    // How far the pointer may travel between press and release and still count as a click, not a selection drag.
    private const double ClickSlop = 4;

    /// <summary>A char-range inside a prose block that resolves to a real file, and the line it cites (0 = none).</summary>
    public sealed record FileSpan(int Start, int Length, string AbsPath, int Line = 0);

    /// <summary>Whether a path is a Markdown file (so it gets the in-app viewer affordances).</summary>
    public static bool IsMarkdown(string path) =>
        path.EndsWith(".md", System.StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".markdown", System.StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Arms a whole <paramref name="target"/> control for <paramref name="absPath"/>. <paramref name="primary"/>,
    /// when set, is the left-click action (marked handled so it doesn't reach an enclosing click, e.g. a tool
    /// card's expand toggle). A tooltip shows the full path.
    /// </summary>
    public static void Attach(Control target, string absPath,
        System.Action<string> openInViewer, System.Action<string> viewDiff, System.Action? primary)
    {
        target.ContextMenu = BuildMenu(target, absPath, 0, openInViewer, viewDiff);
        ToolTip.SetTip(target, absPath);

        if (primary is { } act)
        {
            target.Cursor = new Cursor(StandardCursorType.Hand);
            target.PointerReleased += (_, e) =>
            {
                if (e.InitialPressMouseButton != MouseButton.Left) return;
                act();
                e.Handled = true;   // don't let it bubble to a card's expand toggle
            };
        }
    }

    /// <summary>
    /// Arms file-path <paramref name="spans"/> inside a prose <see cref="SelectableTextBlock"/>: a right-click
    /// over a span opens its context menu, and a <em>Ctrl</em>+left-click opens it (<see cref="Open"/>), mirroring
    /// <see cref="LinkText"/> so a plain click still selects text. Hovering a span shows its path as a tip, with a
    /// hand cursor while Ctrl is held. A no-op with no spans.
    /// </summary>
    public static void AttachInline(SelectableTextBlock tb, IReadOnlyList<FileSpan> spans,
        System.Action<string> openViewer, System.Action<string> viewDiff)
    {
        if (spans.Count == 0) return;

        FileSpan? SpanAt(Point p)
        {
            if (tb.TextLayout is not { } layout) return null;
            var hit = layout.HitTestPoint(new Point(p.X - tb.Padding.Left, p.Y - tb.Padding.Top));
            int idx = hit.TextPosition;
            foreach (var s in spans)
                if (idx >= s.Start && idx < s.Start + s.Length)
                    return s;
            return null;
        }

        tb.AddHandler(Control.ContextRequestedEvent, (object? _, ContextRequestedEventArgs e) =>
        {
            if (!e.TryGetPosition(tb, out var p) || SpanAt(p) is not { } span) return;
            var menu = BuildMenu(tb, span.AbsPath, span.Line, openViewer, viewDiff);
            menu.Placement = PlacementMode.Pointer;
            menu.Open(tb);
            e.Handled = true;
        });

        // The tip/cursor this handler last set, so it only ever clears its own — a URL link armed on the same block
        // (LinkText, whose handler runs first) owns them while the pointer is over a link.
        string? ownTip = null;
        void ClearOwn()
        {
            if (ownTip is null) return;
            if (Equals(ToolTip.GetTip(tb), ownTip)) { ToolTip.SetTip(tb, null); tb.Cursor = null; }
            ownTip = null;
        }
        tb.PointerMoved += (_, e) =>
        {
            if (SpanAt(e.GetPosition(tb)) is not { } span) { ClearOwn(); return; }
            ownTip = (span.Line > 0 ? $"{span.AbsPath}:{span.Line}" : span.AbsPath) + "\nCtrl+click to open · right-click for more";
            if (!Equals(ToolTip.GetTip(tb), ownTip)) ToolTip.SetTip(tb, ownTip);
            tb.Cursor = e.KeyModifiers.HasFlag(KeyModifiers.Control) ? HandCursor : null;
        };
        tb.PointerExited += (_, _) => ClearOwn();

        // handledEventsToo: SelectableTextBlock marks every press handled (it starts a selection).
        Point press = default;
        tb.AddHandler(InputElement.PointerPressedEvent, (_, e) => press = e.GetPosition(tb),
            RoutingStrategies.Bubble, handledEventsToo: true);
        tb.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton != MouseButton.Left) return;
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
            var p = e.GetPosition(tb);
            if (!string.IsNullOrEmpty(tb.SelectedText) || Dist(p, press) > ClickSlop) return;   // a Ctrl+drag selects
            if (SpanAt(p) is { } span)
            {
                Open(span.AbsPath, span.Line, openViewer);
                e.Handled = true;
            }
        };
    }

    /// <summary>The primary open: a Markdown file in the in-app viewer, anything else in VS Code at its line.
    /// Never a shell execute — <c>OpenInEditor</c>'s own fallback is the view-only default handler, else a reveal.</summary>
    private static void Open(string absPath, int line, System.Action<string> openViewer)
    {
        if (IsMarkdown(absPath)) openViewer(absPath);
        else PlatformServices.FileRevealer.OpenInEditor(absPath, line);
    }

    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);

    private static ContextMenu BuildMenu(Control owner, string absPath, int line,
        System.Action<string> openViewer, System.Action<string> viewDiff)
    {
        var menu = new ContextMenu();
        if (IsMarkdown(absPath)) menu.Items.Add(Item("View", () => openViewer(absPath)));
        menu.Items.Add(Item("View diff", () => viewDiff(absPath)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(RevealLabel, () => PlatformServices.FileRevealer.RevealInFileManager(absPath)));
        menu.Items.Add(Item(line > 0 ? $"Open in VS Code at line {line}" : "Open in VS Code",
            () => PlatformServices.FileRevealer.OpenInEditor(absPath, line)));
        menu.Items.Add(Item("Copy path", () => TopLevel.GetTopLevel(owner)?.Clipboard?.SetTextAsync(absPath)));
        return menu;
    }

    private static MenuItem Item(string header, System.Action onClick)
    {
        var mi = new MenuItem { Header = header };
        mi.Click += (_, _) => onClick();
        return mi;
    }

    private static double Dist(Point a, Point b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return System.Math.Sqrt(dx * dx + dy * dy);
    }

    // Windows/Linux say "Explorer"; macOS says "Finder". (Linux managers vary, but "Explorer" reads clearly.)
    private static string RevealLabel =>
        System.OperatingSystem.IsMacOS() ? "Reveal in Finder" : "Reveal in Explorer";
}

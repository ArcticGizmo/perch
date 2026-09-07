using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;

namespace Perch.Avalonia.Views;

/// <summary>
/// Makes a file reference actionable: a right-click context menu — <em>View</em> (Markdown only),
/// <em>View diff</em>, <em>Reveal in Explorer</em> (Finder on macOS), <em>Open in VS Code</em> — plus a
/// left-click primary for a whole-control reference. <em>View</em>/<em>View diff</em> route out via the
/// caller's callbacks (the app owns the viewer/tree windows); reveal/editor go straight to
/// <see cref="PlatformServices.FileRevealer"/>, the same direct-to-platform shape <see cref="LinkText"/> uses
/// for URLs.
///
/// <para>Two shapes: <see cref="Attach"/> arms a whole control (a tool card's filename, a changed-files row);
/// <see cref="AttachInline"/> arms character ranges inside a <see cref="SelectableTextBlock"/> (file paths
/// written as inline code in Claude's prose), following <see cref="LinkText"/>'s <em>Ctrl+click</em>
/// convention so the surrounding text stays selectable.</para>
/// </summary>
internal static class FileRef
{
    /// <summary>A char-range inside a prose block that resolves to a real file.</summary>
    public sealed record FileSpan(int Start, int Length, string AbsPath);

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
        target.ContextMenu = BuildMenu(absPath, openInViewer, viewDiff);
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
    /// over a span opens its context menu, and a <em>Ctrl</em>+left-click over a Markdown span opens the
    /// viewer (mirroring <see cref="LinkText"/>, so a plain click still selects text). No hover cursor is
    /// taken, so this never fights a URL link armed on the same block. A no-op with no spans.
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
            var menu = BuildMenu(span.AbsPath, openViewer, viewDiff);
            menu.Placement = PlacementMode.Pointer;
            menu.Open(tb);
            e.Handled = true;
        });

        tb.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton != MouseButton.Left) return;
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
            if (!string.IsNullOrEmpty(tb.SelectedText)) return;   // a Ctrl+drag selection isn't an open
            if (SpanAt(e.GetPosition(tb)) is { } span && IsMarkdown(span.AbsPath))
            {
                openViewer(span.AbsPath);
                e.Handled = true;
            }
        };
    }

    private static ContextMenu BuildMenu(string absPath, System.Action<string> openViewer, System.Action<string> viewDiff)
    {
        var menu = new ContextMenu();
        if (IsMarkdown(absPath)) menu.Items.Add(Item("View", () => openViewer(absPath)));
        menu.Items.Add(Item("View diff", () => viewDiff(absPath)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(RevealLabel, () => PlatformServices.FileRevealer.RevealInFileManager(absPath)));
        menu.Items.Add(Item("Open in VS Code", () => PlatformServices.FileRevealer.OpenInEditor(absPath)));
        return menu;
    }

    private static MenuItem Item(string header, System.Action onClick)
    {
        var mi = new MenuItem { Header = header };
        mi.Click += (_, _) => onClick();
        return mi;
    }

    // Windows/Linux say "Explorer"; macOS says "Finder". (Linux managers vary, but "Explorer" reads clearly.)
    private static string RevealLabel =>
        System.OperatingSystem.IsMacOS() ? "Reveal in Finder" : "Reveal in Explorer";
}

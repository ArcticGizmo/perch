using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;

namespace Perch.Avalonia.Windows;

/// <summary>
/// Turns a stock <see cref="AutoCompleteBox"/> into a VSCode "quick open"-style project-folder search: an
/// empty box (a 0-char search) offers the whole recent-projects list, typing filters it (case-insensitive
/// substring on the full path), and each row shows the leaf folder bold with its parent path dimmed and
/// ellipsized. The two visual bugs the Fluent default has here are fixed too: the suggestions popup is
/// <b>pinned to the input's own width</b> (its template only sets a <c>MinWidth</c>, so a long path otherwise
/// blows the dropdown out past the field), and the inner text is vertically centred.
/// </summary>
internal static class FolderSearchBox
{
    /// <param name="borderless">Strip the inner TextBox's chrome so the control blends into a surrounding
    /// frame (the rich launcher), rather than keeping the Fluent border (the terminal launcher).</param>
    public static void Configure(
        AutoCompleteBox box, string placeholder,
        IBrush text, IBrush muted, IBrush surface, IBrush border,
        FontFamily body, FontFamily mono, bool borderless)
    {
        box.FilterMode = AutoCompleteFilterMode.Contains;   // case-insensitive substring match on the path
        box.MinimumPrefixLength = 0;                        // empty box → the full recent-projects list
        box.IsTextCompletionEnabled = false;                // suggest a list; never auto-overtype the box
        box.MaxDropDownHeight = 320;

        // On Windows, fold typed/pasted forward slashes to backslashes so a "c:/foo" reads and matches the
        // stored "c:\foo" suggestions. Left as-is elsewhere, where "/" is the real separator. The replacement
        // preserves length, so the caret stays put; the re-entrant TextChanged no-ops (no "/" left to fold).
        if (OperatingSystem.IsWindows())
        {
            box.TextChanged += (_, _) =>
            {
                var t = box.Text;
                if (string.IsNullOrEmpty(t) || !t.Contains('/')) return;
                var caret = box.CaretIndex;
                box.Text = t.Replace('/', '\\');
                box.CaretIndex = caret;
            };
        }

        box.ItemTemplate = new FuncDataTemplate<string>((path, _) =>
        {
            var trimmed = (path ?? string.Empty).TrimEnd('\\', '/');
            var leaf = System.IO.Path.GetFileName(trimmed);
            if (string.IsNullOrEmpty(leaf)) leaf = trimmed;   // a drive root has no leaf
            var parent = System.IO.Path.GetDirectoryName(trimmed) ?? string.Empty;

            var name = new TextBlock
            {
                Text = leaf, FontFamily = body, FontSize = 13, Foreground = text,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0),
            };
            DockPanel.SetDock(name, Dock.Left);
            var dir = new TextBlock
            {
                Text = parent, FontFamily = mono, FontSize = 11.5, Foreground = muted,
                VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
            };
            return new DockPanel { LastChildFill = true, Children = { name, dir } };
        }, supportsRecycling: true);

        box.TemplateApplied += (_, e) =>
        {
            if (e.NameScope.Find<TextBox>("PART_TextBox") is { } inner)
            {
                // AutoCompleteBox's own placeholder property is the obsolete one banned by UiConventionTests,
                // so set the inner TextBox's PlaceholderText (a LocalValue that outranks the template binding).
                inner.PlaceholderText = placeholder;
                inner.VerticalContentAlignment = VerticalAlignment.Center;
                inner.MinHeight = 0;
                if (borderless)
                {
                    inner.Background = Brushes.Transparent;
                    inner.BorderThickness = new Thickness(0);
                    inner.Padding = new Thickness(0);
                }
            }

            // Pin the dropdown to the input's own width and theme it to the surface, so a long path
            // ellipsizes inside the box instead of blowing the popup out past the field.
            if (e.NameScope.Find<Border>("PART_SuggestionsContainer") is { } cont)
            {
                cont.Background = surface;
                cont.BorderBrush = border;
                cont.BorderThickness = new Thickness(1);
                cont.Bind(Layoutable.MaxWidthProperty, new Binding("Bounds.Width") { Source = box });
            }
            if (e.NameScope.Find<ListBox>("PART_SelectingItemsControl") is { } list)
                ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        };
    }
}

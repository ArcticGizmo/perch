using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Perch.Avalonia.Rendering;
using Perch.Avalonia.Theming;
using Perch.Data.Roost;

namespace Perch.Avalonia.Views;

/// <summary>
/// The Roost's tab painter (docs/roost-tabs-plan.md T6), shown in place of a tab's stage while its layout is edited.
/// Left: the built-in presets and the user's saved layouts (pick one to start from, save the current one). Centre:
/// the layout at the stage's real aspect (<see cref="RoostPaintCanvas"/>): hover a region to split or remove it,
/// drag a divider to resize. Footer: the region count, Cancel and Done.
///
/// <para>Only a working copy is edited; <see cref="Finished"/> hands back the result on Done (null on Cancel / Esc)
/// and the window applies it with <see cref="RoostTabSet.ApplyLayout"/>, so sessions follow region ids (D9).</para>
/// </summary>
internal sealed class RoostLayoutPainter : DockPanel
{
    private readonly SessionPalette _p;
    private readonly RoostGridLayout _original;
    private readonly RoostLayoutLibrary _library;
    private readonly RoostPaintCanvas _canvas;
    private readonly StackPanel _presets = new() { Spacing = 2 };
    private readonly StackPanel _saved = new() { Spacing = 2 };
    private readonly TextBlock _count;
    private readonly Border _saveRow;
    private readonly TextBox _saveName;
    private readonly TextBlock _saveButtonText;
    // Each preset / saved row with its layout's geometry key, to light the ones the working copy matches.
    private readonly List<(string Key, Border Row)> _rows = [];
    // A saved layout being renamed in its row (the name it had), or null.
    private string? _renaming;
    // Ids handed to new regions only ever grow, so a region removed earlier in this edit — whose session goes back
    // to the rail — never has its id (and so its session) come back on a later split.
    private int _nextId;
    private bool _finished;

    public RoostLayoutPainter(SessionPalette p, RoostTab tab, RoostLayoutLibrary library, Func<int, string?> caption, double aspect)
    {
        _p = p;
        _original = tab.Layout;
        _library = library;
        TabId = tab.Id;
        _nextId = _original.MaxId + 1;
        LastChildFill = true;
        Background = p.Ground;

        _canvas = new RoostPaintCanvas(p, caption, NewId) { Layout = _original, Aspect = aspect };
        _canvas.Edited += _ => OnLayoutChanged();

        // ── Left: presets, saved layouts, "Save layout…" ──
        (_saveRow, _saveName, _saveButtonText) = SaveRow();
        var strip = new StackPanel
        {
            Spacing = 4, Margin = new Thickness(10, 12, 10, 12),
            Children = { Heading("PRESETS"), _presets, Heading("SAVED", top: 12), _saved, _saveRow },
        };
        Children.Add(new Border
        {
            Width = 196, Background = p.Raised, BorderBrush = p.Border, BorderThickness = new Thickness(0, 0, 1, 0),
            [DockProperty] = Dock.Left,
            Child = new ScrollViewer { HorizontalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Content = strip },
        });

        // ── Footer: count · hint ········ Cancel · Done ──
        _count = new TextBlock { FontFamily = p.Mono, FontSize = 11.5, Foreground = p.Muted, VerticalAlignment = VerticalAlignment.Center };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, [DockProperty] = Dock.Right,
            Children = { Button("Cancel", primary: false, Cancel, "Put the layout back (Esc)"), Button("Done", primary: true, Done, "Use this layout (Enter)") },
        };
        Children.Add(new Border
        {
            Background = p.Raised, BorderBrush = p.Border, BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(14, 7), [DockProperty] = Dock.Bottom,
            Child = new DockPanel
            {
                LastChildFill = true,
                Children =
                {
                    buttons,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal, Spacing = 14, VerticalAlignment = VerticalAlignment.Center,
                        Children =
                        {
                            _count,
                            new TextBlock
                            {
                                Text = "Hover a region to split or remove it  ·  drag a divider to resize",
                                FontFamily = p.Body, FontSize = 12, Foreground = p.Faint, VerticalAlignment = VerticalAlignment.Center,
                                TextTrimming = TextTrimming.CharacterEllipsis,
                            },
                        },
                    },
                },
            },
        });

        Children.Add(new DockPanel
        {
            LastChildFill = true,
            Children =
            {
                new TextBlock
                {
                    Text = $"Editing the layout of {tab.Name}", FontFamily = p.Display, FontWeight = FontWeight.Bold, FontSize = 14,
                    Foreground = p.Title, Margin = new Thickness(18, 12, 18, 0), [DockProperty] = Dock.Top,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                },
                _canvas,
            },
        });

        RebuildStrip();
        OnLayoutChanged();
    }

    /// <summary>The tab being edited.</summary>
    public string TabId { get; }

    /// <summary>The working copy.</summary>
    public RoostGridLayout Working => _canvas.Layout;

    /// <summary>The edit ended: the new layout on Done, null on Cancel.</summary>
    public event Action<RoostGridLayout?>? Finished;

    public void Done() => Finish(Working);

    public void Cancel() => Finish(null);

    private void Finish(RoostGridLayout? result)
    {
        if (_finished) return;
        _finished = true;
        Finished?.Invoke(result);
    }

    private int NewId() => _nextId++;

    // Starting from a preset or saved layout: its regions take the tab's region ids in reading order, so the tab's
    // sessions land in the same reading positions (D9). Mapped from the layout the edit began with, so trying one
    // preset then another doesn't lose sessions along the way.
    private void Use(RoostGridLayout layout)
    {
        _canvas.Layout = layout.AdoptIds(_original);
        OnLayoutChanged();
    }

    private void OnLayoutChanged()
    {
        _nextId = Math.Max(_nextId, Working.MaxId + 1);
        _count.Text = $"{Working.Regions.Count} / {RoostGridLayout.MaxRegions} regions";
        var key = Working.GeometryKey;
        foreach (var (k, row) in _rows) MarkRow(row, k == key);
        _saveButtonText.Text = _library.Contains(_saveName.Text ?? "") ? "Replace" : "Save";
    }

    // ── Strip ─────────────────────────────────────────────────────────────────

    private void RebuildStrip()
    {
        _presets.Children.Clear();
        _saved.Children.Clear();
        _rows.Clear();
        foreach (var (_, name, layout) in RoostGridLayout.Presets) _presets.Children.Add(LayoutRow(name, layout, saved: false));
        if (_library.Saved.Count == 0)
            _saved.Children.Add(new TextBlock
            {
                Text = "Layouts you save show up here.", FontFamily = _p.Body, FontSize = 11.5, Foreground = _p.Faint,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6, 0, 6, 4),
            });
        foreach (var (name, layout) in _library.Saved) _saved.Children.Add(LayoutRow(name, layout, saved: true));
        _saveRow.IsVisible = _library.Saved.Count < RoostLayoutLibrary.MaxSaved || _library.Contains(_saveName.Text ?? "");
        OnLayoutChanged();
    }

    private Border LayoutRow(string name, RoostGridLayout layout, bool saved)
    {
        var label = new TextBlock
        {
            Text = name, FontFamily = _p.Body, FontSize = 12.5, FontWeight = FontWeight.SemiBold, Foreground = _p.Text,
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(10, 0, 0, 0),
        };
        var dock = new DockPanel
        {
            LastChildFill = true,
            Children = { new RoostLayoutThumb(_p) { Layout = layout, Width = 52, Height = 33, [DockProperty] = Dock.Left }, label },
        };
        var row = new Border
        {
            CornerRadius = new CornerRadius(8), Padding = new Thickness(6, 5), BorderThickness = new Thickness(1),
            BorderBrush = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand), Child = dock,
            [ToolTip.TipProperty] = saved ? $"{name} — right-click to rename or delete" : name,
        };
        row.PointerEntered += (_, _) => { if (row.Tag is not true) row.Background = _p.Raised2; };
        row.PointerExited += (_, _) => { if (row.Tag is not true) row.Background = Brushes.Transparent; };
        row.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton != MouseButton.Left || _renaming == name) return;
            e.Handled = true;
            Use(layout);
        };
        if (saved)
        {
            var rename = new MenuItem { Header = "Rename" };
            rename.Click += (_, _) => BeginRenameSaved(name, row, dock, label);
            var delete = new MenuItem { Header = "Delete" };
            delete.Click += (_, _) => { if (_library.Delete(name)) RebuildStrip(); };
            row.ContextFlyout = new MenuFlyout { Items = { rename, delete } };
        }
        // Every row with the working copy's shape lights up (a saved layout shaped like a preset lights both).
        _rows.Add((layout.GeometryKey, row));
        return row;
    }

    private void MarkRow(Border row, bool on)
    {
        row.Tag = on;
        row.Background = on ? _p.BrandWash : row.IsPointerOver ? _p.Raised2 : Brushes.Transparent;
        row.BorderBrush = on ? _p.BrandLine : Brushes.Transparent;
    }

    // A saved row's name, edited in place.
    private void BeginRenameSaved(string name, Border row, DockPanel dock, TextBlock label)
    {
        _renaming = name;
        var box = NameBox(name);
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; Commit(); }
            else if (e.Key == Key.Escape) { e.Handled = true; _renaming = null; RebuildStrip(); }
        };
        box.LostFocus += (_, _) => { if (_renaming == name) Commit(); };
        dock.Children.Remove(label);
        dock.Children.Add(box);
        box.Focus();
        box.SelectAll();

        void Commit()
        {
            _renaming = null;
            _library.Rename(name, box.Text ?? "");
            RebuildStrip();
        }
    }

    // "Save layout…": a name box and a Save (or Replace, when the name is taken) button.
    private (Border, TextBox, TextBlock) SaveRow()
    {
        var box = NameBox("");
        box.PlaceholderText = "Name this layout";
        var text = new TextBlock { Text = "Save", FontFamily = _p.Body, FontWeight = FontWeight.SemiBold, FontSize = 12, Foreground = _p.Brand };
        var save = new Border
        {
            CornerRadius = new CornerRadius(7), Padding = new Thickness(9, 4), BorderThickness = new Thickness(1), BorderBrush = _p.BrandLine,
            Cursor = new Cursor(StandardCursorType.Hand), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0),
            [DockProperty] = Dock.Right, Child = text,
        };
        void Save()
        {
            if (!_library.Save(box.Text ?? "", Working)) return;
            box.Text = "";
            RebuildStrip();
        }
        save.PointerReleased += (_, e) => { if (e.InitialPressMouseButton == MouseButton.Left) { e.Handled = true; Save(); } };
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; Save(); }
            else if (e.Key == Key.Escape) { e.Handled = true; box.Text = ""; Focus(); }
        };
        box.TextChanged += (_, _) => text.Text = _library.Contains(box.Text ?? "") ? "Replace" : "Save";
        var row = new Border
        {
            Margin = new Thickness(0, 8, 0, 0),
            Child = new DockPanel { LastChildFill = true, Children = { save, box } },
        };
        return (row, box, text);
    }

    private TextBox NameBox(string text) => new()
    {
        Text = text, MaxLength = RoostTabSet.MaxNameLength, FontFamily = _p.Body, FontSize = 12, MinHeight = 0,
        Padding = new Thickness(6, 3), VerticalAlignment = VerticalAlignment.Center,
    };

    private TextBlock Heading(string text, double top = 0) => new()
    {
        Text = text, FontFamily = _p.Mono, FontSize = 10.5, LetterSpacing = 1.2, Foreground = _p.Faint,
        Margin = new Thickness(6, top, 6, 4),
    };

    private Border Button(string label, bool primary, Action onClick, string tip)
    {
        var b = new Border
        {
            CornerRadius = new CornerRadius(9), Padding = new Thickness(14, 5), BorderThickness = new Thickness(1),
            BorderBrush = primary ? _p.Brand : _p.Border, Background = primary ? _p.Brand : _p.Surface,
            Cursor = new Cursor(StandardCursorType.Hand), [ToolTip.TipProperty] = tip,
            Child = new TextBlock
            {
                Text = label, FontFamily = _p.Body, FontWeight = FontWeight.SemiBold, FontSize = 12.5,
                Foreground = primary ? _p.BrandInk : _p.Text,
            },
        };
        b.PointerReleased += (_, e) => { if (e.InitialPressMouseButton == MouseButton.Left) { e.Handled = true; onClick(); } };
        return b;
    }

    // ── HeadlessRenderer hooks ────────────────────────────────────────────────

    internal void UseForRender(RoostGridLayout layout) => Use(layout);

    internal void HoverForRender(int regionId, RoostPaintButton button) => _canvas.HoverForRender(regionId, button);

    internal void ClickForRender(int regionId, RoostPaintButton button) => _canvas.ClickForRender(regionId, button);

    internal void DragForRender(RoostDivider divider, int to, bool release) => _canvas.DragForRender(divider, to, release);

    internal void TypeSaveNameForRender(string name) { _saveName.Text = name; }

    internal void SaveForRender()
    {
        if (_library.Save(_saveName.Text ?? "", Working)) { _saveName.Text = ""; RebuildStrip(); }
    }
}

/// <summary>A hovered region's buttons in the painter.</summary>
internal enum RoostPaintButton
{
    None,
    SplitColumns,
    SplitRows,
    Remove,
}

/// <summary>
/// The painter's canvas: the layout drawn at the stage's aspect, owner-drawn (one measured pass, no child controls).
/// Each region shows its session (or "empty") and its Alt chord; hovering one shows split / split / remove buttons
/// (dimmed when the edit isn't allowed); each divider has a grip and drags along the 12-unit grid, showing the unit
/// lines while it moves. Every edit goes through <see cref="RoostGridLayout"/>, so it can't produce an invalid layout.
/// </summary>
internal sealed class RoostPaintCanvas : Control
{
    private const double Pad = 18, Gap = 10, Radius = 10, ButtonSize = 26, ButtonGap = 4, GripReach = 7;

    private readonly SessionPalette _p;
    private readonly Func<int, string?> _caption;
    private readonly Func<int> _newId;
    private RoostGridLayout _layout = RoostGridLayout.Full;
    private int? _hoverRegion;
    private RoostPaintButton _hoverButton;
    private RoostDivider? _hoverDivider;
    // A divider drag: the layout and divider it started from (MoveDivider works from those, clamped), and where it is.
    private (RoostGridLayout From, RoostDivider Divider, int Line)? _drag;

    public RoostPaintCanvas(SessionPalette p, Func<int, string?> caption, Func<int> newId)
    {
        _p = p;
        _caption = caption;
        _newId = newId;
        ClipToBounds = true;
        Focusable = false;
    }

    /// <summary>A split, removal or divider move changed the layout.</summary>
    public event Action<RoostGridLayout>? Edited;

    public RoostGridLayout Layout
    {
        get => _layout;
        set
        {
            _layout = value;
            if (_hoverRegion is { } h && value.Find(h) is null) _hoverRegion = null;
            _hoverDivider = null;
            InvalidateVisual();
        }
    }

    /// <summary>The stage's width / height, so the canvas draws the layout at the shape it'll really have.</summary>
    public double Aspect { get; set; } = 1.6;

    // ── Geometry ──────────────────────────────────────────────────────────────

    // The board: the largest rect of the stage's aspect inside the padded bounds, centred.
    private Rect Board()
    {
        var area = new Rect(Bounds.Size).Deflate(Pad);
        if (area.Width <= 0 || area.Height <= 0) return default;
        double aspect = Aspect > 0.2 ? Aspect : 1.6;
        double w = Math.Min(area.Width, area.Height * aspect), h = w / aspect;
        return new Rect(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h);
    }

    private static Rect RegionRect(Rect board, RoostRegion r)
    {
        double ux = board.Width / RoostGridLayout.Units, uy = board.Height / RoostGridLayout.Units;
        return new Rect(board.X + r.Column * ux, board.Y + r.Row * uy, r.ColumnSpan * ux, r.RowSpan * uy).Deflate(Gap / 2);
    }

    // A divider's line segment on the board.
    private static (Point A, Point B) DividerLine(Rect board, RoostDivider d, int line)
    {
        double ux = board.Width / RoostGridLayout.Units, uy = board.Height / RoostGridLayout.Units;
        return d.Axis == RoostAxis.Vertical
            ? (new Point(board.X + line * ux, board.Y + d.Start * uy), new Point(board.X + line * ux, board.Y + d.End * uy))
            : (new Point(board.X + d.Start * ux, board.Y + line * uy), new Point(board.X + d.End * ux, board.Y + line * uy));
    }

    // The hovered region's buttons, right to left from its top-right corner: remove, split rows, split columns.
    private static Rect ButtonRect(Rect region, RoostPaintButton b)
    {
        int fromRight = b switch { RoostPaintButton.Remove => 0, RoostPaintButton.SplitRows => 1, _ => 2 };
        double x = region.Right - 8 - ButtonSize - fromRight * (ButtonSize + ButtonGap);
        return new Rect(x, region.Y + 8, ButtonSize, ButtonSize);
    }

    private bool Allowed(int regionId, RoostPaintButton b) => b switch
    {
        RoostPaintButton.SplitColumns => _layout.CanSplit(regionId, RoostSplit.Columns),
        RoostPaintButton.SplitRows => _layout.CanSplit(regionId, RoostSplit.Rows),
        RoostPaintButton.Remove => _layout.CanRemove(regionId),
        _ => false,
    };

    private static string Tip(RoostPaintButton b, bool allowed) => (b, allowed) switch
    {
        (RoostPaintButton.SplitColumns, true) => "Split side by side",
        (RoostPaintButton.SplitRows, true) => "Split top and bottom",
        (RoostPaintButton.Remove, true) => "Remove — a neighbour grows into its space (its session goes back to the rail)",
        (RoostPaintButton.Remove, false) => "Can't remove: no neighbour shares a whole side with it",
        (_, false) => "Too small to split (or the tab has 8 regions)",
        _ => "",
    };

    // ── Paint ─────────────────────────────────────────────────────────────────

    public override void Render(DrawingContext ctx)
    {
        var board = Board();
        if (board.Width <= 0) return;

        var order = _layout.ReadingOrder;
        for (int i = 0; i < order.Count; i++)
        {
            var region = order[i];
            var r = RegionRect(board, region);
            bool hover = _drag is null && _hoverRegion == region.Id;
            OverlayDraw.Panel(ctx, r, hover ? _p.Raised2 : _p.Raised, OverlayDraw.Pen(hover ? _p.BrandLine : _p.Border, 1.5), Radius);
            PaintCaption(ctx, r, region.Id, i + 1);
            if (hover)
                foreach (var b in new[] { RoostPaintButton.SplitColumns, RoostPaintButton.SplitRows, RoostPaintButton.Remove })
                    PaintButton(ctx, ButtonRect(r, b), b, Allowed(region.Id, b), _hoverButton == b);
        }

        // While dragging, the unit lines it can snap to show over the regions.
        if (_drag is not null)
        {
            var grid = OverlayDraw.Pen(OverlayDraw.Brush(_p.Muted.Color, 0.28), 1);
            for (int i = 1; i < RoostGridLayout.Units; i++)
            {
                double x = board.X + i * board.Width / RoostGridLayout.Units, y = board.Y + i * board.Height / RoostGridLayout.Units;
                ctx.DrawLine(grid, new Point(x, board.Y), new Point(x, board.Bottom));
                ctx.DrawLine(grid, new Point(board.X, y), new Point(board.Right, y));
            }
        }

        // While dragging, only the moving divider shows: its run from the layout the drag started on, at the line
        // it's at now (the regions are already the moved layout). Otherwise every divider shows its grip.
        if (_drag is { } drag) PaintDivider(ctx, board, drag.Divider, drag.Line, live: true);
        else foreach (var d in _layout.Dividers) PaintDivider(ctx, board, d, d.Line, live: _hoverDivider == d);
    }

    private void PaintDivider(DrawingContext ctx, Rect board, RoostDivider d, int line, bool live)
    {
        var (a, b) = DividerLine(board, d, line);
        if (live) ctx.DrawLine(OverlayDraw.Pen(_p.Brand, 3, lineCap: PenLineCap.Round), a, b);
        // A grip at the middle says "this moves".
        var mid = new Point((a.X + b.X) / 2, (a.Y + b.Y) / 2);
        var grip = d.Axis == RoostAxis.Vertical ? new Rect(mid.X - 2.5, mid.Y - 12, 5, 24) : new Rect(mid.X - 12, mid.Y - 2.5, 24, 5);
        OverlayDraw.Pill(ctx, live ? _p.Brand : _p.Muted, grip);
    }

    private void PaintCaption(DrawingContext ctx, Rect r, int regionId, int number)
    {
        var name = _caption(regionId);
        var title = OverlayDraw.Text(OverlayDraw.Truncate(name ?? "empty", 13, Math.Max(20, r.Width - 24), FontWeight.SemiBold),
            13, name is null ? _p.Faint : _p.Text, FontWeight.SemiBold);
        var sub = OverlayDraw.Text(RoostKeys.ChordFor(RoostCommand.Region, number) ?? $"Region {number}", 11, _p.Faint);
        double h = title.Height + 3 + sub.Height;
        if (h > r.Height - 8) return;   // too short to label without clipping
        double top = r.Y + (r.Height - h) / 2;
        ctx.DrawText(title, new Point(r.X + (r.Width - title.Width) / 2, top));
        ctx.DrawText(sub, new Point(r.X + (r.Width - sub.Width) / 2, top + title.Height + 3));
    }

    private void PaintButton(DrawingContext ctx, Rect b, RoostPaintButton kind, bool allowed, bool hover)
    {
        var ink = !allowed ? _p.Faint : hover ? _p.Brand : _p.Text;
        OverlayDraw.Panel(ctx, b, hover && allowed ? _p.BrandWash : _p.Surface, OverlayDraw.Pen(hover && allowed ? _p.BrandLine : _p.Border, 1), 7);
        using var _ = ctx.PushOpacity(allowed ? 1 : 0.45);
        var pen = OverlayDraw.Pen(ink, 1.5, lineCap: PenLineCap.Round);
        var icon = b.Deflate(7);
        if (kind == RoostPaintButton.Remove)
        {
            ctx.DrawLine(pen, icon.TopLeft, icon.BottomRight);
            ctx.DrawLine(pen, icon.TopRight, icon.BottomLeft);
            return;
        }
        // A box with its new divider: down the middle (side by side) or across (top and bottom).
        ctx.DrawRectangle(null, pen, new RoundedRect(icon, 2));
        if (kind == RoostPaintButton.SplitColumns) ctx.DrawLine(pen, new Point(icon.Center.X, icon.Y), new Point(icon.Center.X, icon.Bottom));
        else ctx.DrawLine(pen, new Point(icon.X, icon.Center.Y), new Point(icon.Right, icon.Center.Y));
    }

    // ── Pointer ───────────────────────────────────────────────────────────────

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var at = e.GetPosition(this);
        if (_drag is { } drag) { DragTo(drag, at); return; }
        UpdateHover(at);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        UpdateHover(e.GetPosition(this));
        if (_hoverDivider is { } d)
        {
            _drag = (_layout, d, d.Line);
            e.Pointer.Capture(this);
            e.Handled = true;
            InvalidateVisual();
        }
        else if (_hoverRegion is { } id && _hoverButton != RoostPaintButton.None)
        {
            e.Handled = true;
            Click(id, _hoverButton);
            UpdateHover(e.GetPosition(this));
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag is null) return;
        EndDrag();
        e.Pointer.Capture(null);
        UpdateHover(e.GetPosition(this));
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (_drag is not null) EndDrag();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_drag is not null) return;
        _hoverRegion = null;
        _hoverButton = RoostPaintButton.None;
        _hoverDivider = null;
        InvalidateVisual();
    }

    private void UpdateHover(Point at)
    {
        var board = Board();
        RoostDivider? divider = null;
        int? region = null;
        var button = RoostPaintButton.None;
        foreach (var d in _layout.Dividers)
        {
            var (a, b) = DividerLine(board, d, d.Line);
            bool near = d.Axis == RoostAxis.Vertical
                ? Math.Abs(at.X - a.X) <= GripReach && at.Y >= a.Y && at.Y <= b.Y
                : Math.Abs(at.Y - a.Y) <= GripReach && at.X >= a.X && at.X <= b.X;
            if (near) { divider = d; break; }
        }
        if (divider is null)
            foreach (var r in _layout.Regions)
            {
                var rect = RegionRect(board, r);
                if (!rect.Contains(at)) continue;
                region = r.Id;
                foreach (var b in new[] { RoostPaintButton.SplitColumns, RoostPaintButton.SplitRows, RoostPaintButton.Remove })
                    if (ButtonRect(rect, b).Contains(at)) button = b;
                break;
            }
        if (divider == _hoverDivider && region == _hoverRegion && button == _hoverButton) return;
        _hoverDivider = divider;
        _hoverRegion = region;
        _hoverButton = button;
        Cursor = divider is { Axis: RoostAxis.Vertical } ? new Cursor(StandardCursorType.SizeWestEast)
            : divider is not null ? new Cursor(StandardCursorType.SizeNorthSouth)
            : button != RoostPaintButton.None && region is { } id && Allowed(id, button) ? new Cursor(StandardCursorType.Hand)
            : Cursor.Default;
        ToolTip.SetTip(this, divider is not null ? "Drag to resize" : region is { } rid && button != RoostPaintButton.None ? Tip(button, Allowed(rid, button)) : null);
        InvalidateVisual();
    }

    private void Click(int regionId, RoostPaintButton b)
    {
        if (!Allowed(regionId, b)) return;
        var next = b switch
        {
            RoostPaintButton.SplitColumns => _layout.Split(regionId, RoostSplit.Columns, _newId()),
            RoostPaintButton.SplitRows => _layout.Split(regionId, RoostSplit.Rows, _newId()),
            _ => _layout.Remove(regionId),
        };
        if (next is null) return;
        Layout = next;
        Edited?.Invoke(next);
    }

    // The pointer's nearest unit line on the divider's axis; MoveDivider clamps it to the last valid line.
    private void DragTo((RoostGridLayout From, RoostDivider Divider, int Line) drag, Point at)
    {
        var board = Board();
        bool v = drag.Divider.Axis == RoostAxis.Vertical;
        double unit = (v ? board.Width : board.Height) / RoostGridLayout.Units;
        int line = (int)Math.Round(((v ? at.X - board.X : at.Y - board.Y)) / unit);
        var (min, max) = drag.From.DividerRange(drag.Divider);
        line = Math.Clamp(line, min, max);
        if (line == drag.Line) return;
        _drag = drag with { Line = line };
        if (drag.From.MoveDivider(drag.Divider, line) is { } moved)
        {
            _layout = moved;
            Edited?.Invoke(moved);
        }
        InvalidateVisual();
    }

    private void EndDrag()
    {
        _drag = null;
        _hoverDivider = null;
        InvalidateVisual();
    }

    internal void HoverForRender(int regionId, RoostPaintButton button)
    {
        _hoverRegion = regionId;
        _hoverButton = button;
        _hoverDivider = null;
        InvalidateVisual();
    }

    internal void ClickForRender(int regionId, RoostPaintButton button) => Click(regionId, button);

    internal void DragForRender(RoostDivider divider, int to, bool release)
    {
        _drag = (_layout, divider, divider.Line);
        var board = Board();
        bool v = divider.Axis == RoostAxis.Vertical;
        double unit = (v ? board.Width : board.Height) / RoostGridLayout.Units;
        DragTo(_drag.Value, v ? new Point(board.X + to * unit, 0) : new Point(0, board.Y + to * unit));
        if (release) EndDrag();
    }
}

/// <summary>A small picture of a layout (the painter's preset and saved rows).</summary>
internal sealed class RoostLayoutThumb : Control
{
    private readonly SessionPalette _p;

    public RoostLayoutThumb(SessionPalette p) => _p = p;

    public RoostGridLayout Layout { get; set; } = RoostGridLayout.Full;

    public override void Render(DrawingContext ctx)
    {
        var b = new Rect(Bounds.Size);
        double ux = b.Width / RoostGridLayout.Units, uy = b.Height / RoostGridLayout.Units;
        var pen = OverlayDraw.Pen(_p.Muted, 1);
        foreach (var r in Layout.Regions)
        {
            var rect = new Rect(r.Column * ux, r.Row * uy, r.ColumnSpan * ux, r.RowSpan * uy).Deflate(1.5);
            OverlayDraw.Panel(ctx, rect, _p.Raised2, pen, 3);
        }
    }
}

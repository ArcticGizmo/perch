using Avalonia;
using Avalonia.Controls;
using Perch.Data.Roost;

namespace Perch.Avalonia.Views;

/// <summary>
/// The Roost's stage: lays each child into a region of a <see cref="RoostGridLayout"/> (the active tab's layout, or
/// <see cref="RoostGridLayout.Full"/> while a region is zoomed), picked by the attached <see cref="SlotProperty"/>:
/// slot <c>i</c> is <see cref="RoostGridLayout.Regions"/>[<c>i</c>]. Each child is measured with exactly its region's
/// size, so moving a child to another region of the same size leaves its measured layout valid — a swap re-arranges,
/// it doesn't re-measure the panes' threads.
/// </summary>
internal sealed class RoostTilePanel : Panel
{
    public static readonly AttachedProperty<int> SlotProperty =
        AvaloniaProperty.RegisterAttached<RoostTilePanel, Control, int>("Slot");

    private RoostGridLayout _layout = RoostGridLayout.Full;

    static RoostTilePanel() => AffectsParentMeasure<RoostTilePanel>(SlotProperty);

    public static int GetSlot(Control c) => c.GetValue(SlotProperty);

    public static void SetSlot(Control c, int slot) => c.SetValue(SlotProperty, slot);

    public RoostGridLayout Layout
    {
        get => _layout;
        set
        {
            // Slot order matters (slot i = Regions[i]), so compare the regions in order, not the id-sorted Signature.
            if (value.Regions.SequenceEqual(_layout.Regions)) return;
            _layout = value;
            InvalidateMeasure();
        }
    }

    /// <summary>A region's rectangle in <paramref name="area"/>: its share of the
    /// <see cref="RoostGridLayout.Units"/>-unit grid each way. The stage, the painter and its thumbnails all place
    /// regions with this.</summary>
    public static Rect UnitRect(Rect area, RoostRegion r)
    {
        double ux = area.Width / RoostGridLayout.Units, uy = area.Height / RoostGridLayout.Units;
        return new Rect(area.X + r.Column * ux, area.Y + r.Row * uy, r.ColumnSpan * ux, r.RowSpan * uy);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var size = new Size(
            double.IsInfinity(availableSize.Width) ? 1000 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 700 : availableSize.Height);
        foreach (var child in Children)
            if (CellRect(GetSlot(child), size) is { } r) child.Measure(r.Size);
        return size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (var child in Children)
            if (CellRect(GetSlot(child), finalSize) is { } r) child.Arrange(r);
        return finalSize;
    }

    private Rect? CellRect(int slot, Size size)
    {
        if (slot < 0 || slot >= _layout.Regions.Count) return null;
        var r = UnitRect(new Rect(size), _layout.Regions[slot]);
        // Position and size rounded separately, so equal cells get identical sizes (a moved child's measure stays
        // valid); the half-pixel this can leave between cells falls inside the hosts' margins.
        return new Rect(Math.Round(r.X), Math.Round(r.Y), Math.Round(r.Width), Math.Round(r.Height));
    }
}

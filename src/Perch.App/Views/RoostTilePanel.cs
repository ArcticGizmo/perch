using Avalonia;
using Avalonia.Controls;
using Perch.Data.Roost;

namespace Perch.Avalonia.Views;

/// <summary>
/// The Roost's stage: lays each child into a cell of a <see cref="RoostTemplateShape"/> (the active tab's layout,
/// <see cref="RoostGridLayout.ToShape"/>), picked by the
/// attached <see cref="SlotProperty"/>. Each child is measured with exactly its cell's size, so moving a child to
/// another cell of the same size leaves its measured layout valid — a swap re-arranges, it doesn't re-measure the
/// panes' threads.
/// </summary>
internal sealed class RoostTilePanel : Panel
{
    public static readonly AttachedProperty<int> SlotProperty =
        AvaloniaProperty.RegisterAttached<RoostTilePanel, Control, int>("Slot");

    private RoostTemplateShape _shape = RoostTemplates.Shape(RoostSnapTemplate.Full);

    static RoostTilePanel() => AffectsParentMeasure<RoostTilePanel>(SlotProperty);

    public static int GetSlot(Control c) => c.GetValue(SlotProperty);

    public static void SetSlot(Control c, int slot) => c.SetValue(SlotProperty, slot);

    public RoostTemplateShape Shape
    {
        get => _shape;
        set
        {
            if (value.Signature == _shape.Signature) return;
            _shape = value;
            InvalidateMeasure();
        }
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

    // A slot's rectangle in a panel of this size: its columns' and rows' share of the star weights.
    private Rect? CellRect(int slot, Size size)
    {
        if (slot < 0 || slot >= _shape.Slots.Count) return null;
        var s = _shape.Slots[slot];
        double colSum = _shape.Columns.Sum(), rowSum = _shape.Rows.Sum();
        double x = _shape.Columns.Take(s.Column).Sum() / colSum * size.Width;
        double w = _shape.Columns.Skip(s.Column).Take(s.ColumnSpan).Sum() / colSum * size.Width;
        double y = _shape.Rows.Take(s.Row).Sum() / rowSum * size.Height;
        double h = _shape.Rows.Skip(s.Row).Take(s.RowSpan).Sum() / rowSum * size.Height;
        // Position and size rounded separately, so equal cells get identical sizes (a moved child's measure stays
        // valid); the half-pixel this can leave between cells falls inside the hosts' margins.
        return new Rect(Math.Round(x), Math.Round(y), Math.Round(w), Math.Round(h));
    }
}

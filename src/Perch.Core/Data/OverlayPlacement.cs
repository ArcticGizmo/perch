namespace Perch.Data;

/// <summary>Which horizontal screen edge a placement is measured from.</summary>
public enum HAnchor { Left, Right }

/// <summary>Which vertical screen edge a placement is measured from.</summary>
public enum VAnchor { Top, Bottom }

/// <summary>
/// A user-defined initial position for an overlay presentation, stored relative to the <em>nearest</em>
/// corner of a monitor's work area — an <see cref="HAnchor"/>/<see cref="VAnchor"/> pair plus the DIP
/// distance from each of those two edges. Storing it corner-relative (rather than an absolute point)
/// keeps the overlay pinned to the same visual corner when the resolution or work area changes.
/// <para>
/// The target monitor is recorded as its physical-pixel bounds (mirroring the identity the dense
/// controller already uses); all four are null for "primary / auto". Offsets are in DIP; the concrete
/// physical position is derived by <see cref="PlacementMath"/> against the live work area and scale.
/// Dense mode is edge-docked, so it only meaningfully uses the horizontal anchor and the vertical
/// offset — <see cref="OffsetX"/> is ignored there.
/// </para>
/// </summary>
public sealed class OverlayPlacement
{
    /// <summary>Physical-pixel bounds of the target monitor; all null means primary / auto.</summary>
    public int? MonitorX { get; set; }
    public int? MonitorY { get; set; }
    public int? MonitorW { get; set; }
    public int? MonitorH { get; set; }

    public HAnchor HAnchor { get; set; } = HAnchor.Right;
    public VAnchor VAnchor { get; set; } = VAnchor.Top;

    /// <summary>DIP distance from the anchored horizontal edge to the window's near (left/right) edge —
    /// the width is fixed, so this measures the window edge (ignored in dense mode).</summary>
    public double OffsetX { get; set; }

    /// <summary>DIP distance from the anchored vertical edge to the window's <b>top</b> edge (the header).
    /// The panel height is deliberately not part of this: a taller/shorter panel keeps the same header
    /// position and simply grows downward (clamped on-screen), so the header doesn't jump as the session
    /// count changes. See <see cref="PlacementMath"/>.</summary>
    public double OffsetY { get; set; }

    public OverlayPlacement Clone() => new()
    {
        MonitorX = MonitorX, MonitorY = MonitorY, MonitorW = MonitorW, MonitorH = MonitorH,
        HAnchor = HAnchor, VAnchor = VAnchor, OffsetX = OffsetX, OffsetY = OffsetY,
    };
}

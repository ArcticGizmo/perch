namespace Perch.Data.Mermaid;

/// <summary>
/// The view transform of a pannable, zoomable picture: content point <c>c</c> shows at viewport point
/// <c>c × Zoom + (X, Y)</c>. Pure arithmetic, so the fullscreen diagram viewer's feel (zooming about the cursor,
/// fitting, never losing the picture off-screen) is pinned by tests rather than by hand.
/// </summary>
internal readonly record struct PanZoom(double Zoom, double X, double Y)
{
    public const double MinZoom = 0.1, MaxZoom = 8;

    public static readonly PanZoom Identity = new(1, 0, 0);

    /// <summary>The whole of <paramref name="content"/> centred in <paramref name="viewport"/> with
    /// <paramref name="padding"/> around it, scaled up no further than <paramref name="maxZoom"/> (so a small diagram
    /// doesn't open comically large).</summary>
    public static PanZoom Fit(MSize content, MSize viewport, double padding, double maxZoom)
    {
        if (content.W <= 0 || content.H <= 0 || viewport.W <= 0 || viewport.H <= 0) return Identity;
        double z = Math.Min((viewport.W - 2 * padding) / content.W, (viewport.H - 2 * padding) / content.H);
        return Centered(Math.Clamp(z, MinZoom, Math.Max(MinZoom, maxZoom)), content, viewport);
    }

    /// <summary><paramref name="content"/> at <paramref name="zoom"/>, centred in <paramref name="viewport"/>.</summary>
    public static PanZoom Centered(double zoom, MSize content, MSize viewport)
    {
        double z = Math.Clamp(zoom, MinZoom, MaxZoom);
        return new PanZoom(z, (viewport.W - content.W * z) / 2, (viewport.H - content.H * z) / 2);
    }

    /// <summary>Zooms by <paramref name="factor"/> keeping the content under viewport point <paramref name="at"/>
    /// where it is — the cursor stays on the node it was pointing at.</summary>
    public PanZoom ZoomAt(MPoint at, double factor)
    {
        double z = Math.Clamp(Zoom * factor, MinZoom, MaxZoom);
        double k = z / Zoom;
        return new PanZoom(z, at.X - (at.X - X) * k, at.Y - (at.Y - Y) * k);
    }

    public PanZoom Pan(double dx, double dy) => this with { X = X + dx, Y = Y + dy };

    /// <summary>Keeps at least <paramref name="keep"/> pixels of the content inside the viewport on each axis (all of
    /// it, when it's smaller than that), so a drag or zoom can never fling the picture out of reach.</summary>
    public PanZoom Clamp(MSize content, MSize viewport, double keep)
    {
        double w = content.W * Zoom, h = content.H * Zoom;
        double kx = Math.Min(keep, w), ky = Math.Min(keep, h);
        return this with
        {
            X = Math.Clamp(X, kx - w, Math.Max(kx - w, viewport.W - kx)),
            Y = Math.Clamp(Y, ky - h, Math.Max(ky - h, viewport.H - ky)),
        };
    }
}

using System.Text.Json;
using Perch.Theming;

namespace Perch.Games;

/// <summary>One point of a freehand stroke, in the fixed <see cref="DrawStrokeCodec.CanvasSize"/> coordinate
/// space (0..1000 on each axis) so a drawing renders identically at any window size on either player's
/// screen.</summary>
public readonly record struct DrawPoint(short X, short Y);

/// <summary>One freehand stroke: a colour + brush size (indices into <see cref="DrawPalette"/>) and the run of
/// points the pointer traced. Deliberately UI-free — the drawing canvas builds these from pointer input and the
/// guess view renders them, but the model itself carries no Avalonia types so it can be serialised for the wire
/// (see <see cref="DrawStrokeCodec"/>).</summary>
public sealed record DrawStroke(byte Color, byte Size, IReadOnlyList<DrawPoint> Points);

/// <summary>
/// The fixed drawing palette for "Draw with Perch" — a small, deliberately limited set of ink colours and three
/// brush sizes, kept in <c>Perch.Core</c> as plain <see cref="Rgb"/> so the codec's colour/size <em>indices</em>
/// are bounded head-agnostically. The paper is always white so a drawing looks the same for the drawer and the
/// guesser regardless of their theme; the "eraser" tool is just painting with the paper colour. The app head
/// surfaces these as brushes through <c>Theming.Palette</c>.
/// </summary>
public static class DrawPalette
{
    /// <summary>The index of the paper/white colour (also what the eraser paints with).</summary>
    public const byte PaperIndex = 1;

    /// <summary>The ink colours, indexed by <see cref="DrawStroke.Color"/>. Order is the wire format — append
    /// only, never reorder, or existing drawings recolour.</summary>
    public static readonly IReadOnlyList<Rgb> Colors = new[]
    {
        Rgb.FromHex("#22272E"), // 0 ink (near-black)
        Rgb.FromHex("#FFFFFF"), // 1 paper / eraser
        Rgb.FromHex("#8B95A1"), // 2 grey
        Rgb.FromHex("#E5484D"), // 3 red
        Rgb.FromHex("#F76B15"), // 4 orange
        Rgb.FromHex("#F5C518"), // 5 yellow
        Rgb.FromHex("#30A46C"), // 6 green
        Rgb.FromHex("#12A5A5"), // 7 teal
        Rgb.FromHex("#3E7BFA"), // 8 blue
        Rgb.FromHex("#8E4EC6"), // 9 purple
        Rgb.FromHex("#E93D82"), // 10 pink
        Rgb.FromHex("#8B5E3C"), // 11 brown
    };

    /// <summary>The three brush widths, in canvas units (0..1000 space), indexed by <see cref="DrawStroke.Size"/>.</summary>
    public static readonly IReadOnlyList<double> Sizes = new[] { 8.0, 18.0, 34.0 };

    /// <summary>The paper colour a fresh canvas is filled with (and what the eraser paints).</summary>
    public static Rgb Paper => Colors[PaperIndex];
}

/// <summary>
/// The compact string codec that puts a list of <see cref="DrawStroke"/>s on the wire (the DB <c>strokes</c>
/// column). Pure and UI-free, kept in <c>Perch.Core</c> so both clients round-trip drawings identically.
/// Coordinates are integers in the fixed 0..1000 canvas; decode is deliberately lenient (it clamps coordinates,
/// drops malformed or out-of-range strokes, and caps totals) so a corrupt or oversized payload degrades to a
/// partial drawing rather than throwing out of a render.
/// </summary>
public static class DrawStrokeCodec
{
    /// <summary>The edge length of the normalised drawing canvas; every coordinate is 0..<see cref="CanvasSize"/>.</summary>
    public const int CanvasSize = 1000;

    /// <summary>Upper bound on strokes in one drawing (decode drops the rest); keeps the payload bounded.</summary>
    public const int MaxStrokes = 800;

    /// <summary>Upper bound on points in a single stroke (decode truncates the rest).</summary>
    public const int MaxPointsPerStroke = 2000;

    /// <summary>Upper bound on points across the whole drawing (decode stops once reached).</summary>
    public const int MaxTotalPoints = 12000;

    private const int FormatVersion = 1;

    /// <summary>Encodes the strokes to a compact JSON string: <c>{"v":1,"s":[{"c":0,"z":1,"p":[x,y,x,y,...]}]}</c>.
    /// Colours/sizes out of palette range and coordinates out of canvas range are clamped. An empty/null list
    /// encodes to an empty drawing.</summary>
    public static string Encode(IReadOnlyList<DrawStroke>? strokes)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteNumber("v", FormatVersion);
            w.WriteStartArray("s");
            int total = 0;
            int strokeCount = 0;
            if (strokes is not null)
            {
                foreach (var s in strokes)
                {
                    if (strokeCount >= MaxStrokes || s.Points is null || s.Points.Count == 0) continue;
                    w.WriteStartObject();
                    w.WriteNumber("c", ClampIndex(s.Color, DrawPalette.Colors.Count));
                    w.WriteNumber("z", ClampIndex(s.Size, DrawPalette.Sizes.Count));
                    w.WriteStartArray("p");
                    int pts = 0;
                    foreach (var p in s.Points)
                    {
                        if (pts >= MaxPointsPerStroke || total >= MaxTotalPoints) break;
                        w.WriteNumberValue(ClampCoord(p.X));
                        w.WriteNumberValue(ClampCoord(p.Y));
                        pts++; total++;
                    }
                    w.WriteEndArray();
                    w.WriteEndObject();
                    strokeCount++;
                }
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Decodes a drawing produced by <see cref="Encode"/>. Lenient: malformed JSON yields an empty
    /// drawing, and individual bad strokes/points are clamped or dropped rather than thrown, so a corrupt or
    /// oversized payload never breaks the view.</summary>
    public static IReadOnlyList<DrawStroke> Decode(string? json)
    {
        var result = new List<DrawStroke>();
        if (string.IsNullOrWhiteSpace(json)) return result;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("s", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return result;

            int total = 0;
            foreach (var s in arr.EnumerateArray())
            {
                if (result.Count >= MaxStrokes) break;
                if (s.ValueKind != JsonValueKind.Object) continue;
                byte color = (byte)ClampIndex(ReadInt(s, "c"), DrawPalette.Colors.Count);
                byte size = (byte)ClampIndex(ReadInt(s, "z"), DrawPalette.Sizes.Count);
                if (!s.TryGetProperty("p", out var p) || p.ValueKind != JsonValueKind.Array) continue;

                var points = new List<DrawPoint>();
                short? pendingX = null;
                foreach (var v in p.EnumerateArray())
                {
                    if (points.Count >= MaxPointsPerStroke || total >= MaxTotalPoints) break;
                    if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out int n)) continue;
                    short coord = ClampCoord(n);
                    if (pendingX is null) { pendingX = coord; }
                    else { points.Add(new DrawPoint(pendingX.Value, coord)); pendingX = null; total++; }
                }
                if (points.Count > 0) result.Add(new DrawStroke(color, size, points));
            }
        }
        catch (JsonException) { /* corrupt payload → whatever we parsed so far (usually empty) */ }
        return result;
    }

    private static int ReadInt(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out int n) ? n : 0;

    private static int ClampIndex(int i, int count) => i < 0 ? 0 : i >= count ? 0 : i;

    private static short ClampCoord(int c) => (short)Math.Clamp(c, 0, CanvasSize);
}

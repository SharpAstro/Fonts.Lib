namespace SharpAstro.Fonts.Rasterizer.Msdf;

/// <summary>A closed loop of outline edges. A glyph is one or more contours (outer fill + inner holes).</summary>
internal sealed class Contour
{
    public List<EdgeSegment> Edges { get; } = [];

    public void Add(EdgeSegment edge) => Edges.Add(edge);
}

/// <summary>
/// A glyph outline as contours of edge segments, y pointing up (the
/// FreeType/msdfgen frame). Carries the bounds and a nonzero-winding point test
/// used by the generator's polarity safety net.
/// </summary>
internal sealed class Shape
{
    public List<Contour> Contours { get; } = [];

    public bool IsEmpty => Contours.Count == 0 || Contours.TrueForAll(c => c.Edges.Count == 0);

    /// <summary>Tight axis-aligned ink bounds over all edges (endpoints + curve extrema).</summary>
    public Bounds ComputeBounds()
    {
        var l = double.MaxValue;
        var b = double.MaxValue;
        var r = double.MinValue;
        var t = double.MinValue;
        foreach (var contour in Contours)
            foreach (var edge in contour.Edges)
                edge.ExtendBounds(ref l, ref b, ref r, ref t);
        return new Bounds(l, b, r, t);
    }

    /// <summary>
    /// The nonzero winding number of <paramref name="p"/>: net signed crossings
    /// of a rightward horizontal ray. Nonzero ⇒ inside the filled region,
    /// independent of the font's contour orientation.
    /// </summary>
    public int WindingAt(Vector2D p)
    {
        var winding = 0;
        foreach (var contour in Contours)
            foreach (var edge in contour.Edges)
                edge.AddRayCrossings(p.Y, p.X, ref winding);
        return winding;
    }
}

/// <summary>Axis-aligned bounds (y-up).</summary>
internal readonly record struct Bounds(double Left, double Bottom, double Right, double Top)
{
    public bool IsValid => Right >= Left && Top >= Bottom;
    public double Width => Right - Left;
    public double Height => Top - Bottom;
}

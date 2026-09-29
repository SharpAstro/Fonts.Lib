namespace SharpAstro.Fonts.Rasterizer.Msdf;

/// <summary>A closed loop of outline edges. A glyph is one or more contours (outer fill + inner holes).</summary>
internal sealed class Contour
{
    public List<EdgeSegment> Edges { get; } = [];

    public void Add(EdgeSegment edge) => Edges.Add(edge);

    /// <summary>Traverse the loop the other way: the edges in reverse order, each one reversed.</summary>
    public void Reverse()
    {
        Edges.Reverse();
        for (var i = 0; i < Edges.Count; i++)
            Edges[i] = Edges[i].Reversed();
    }
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
    /// Wind the outline the way TrueType does, outer contours clockwise, reversing every contour when
    /// it is the other way. CFF and Type 1 wind their outer contours counter-clockwise, and a flip of
    /// the finished field cannot stand in for this: the generator's overlapping-contour combiner reads
    /// each contour's winding to tell the outer outline from a counter, and read the wrong way round
    /// it measures every point outside a glyph with a counter (a, e, o) to the COUNTER, across the
    /// stroke, so the field falls from the edge straight to zero and each outer edge draws half a
    /// texel inside the outline. The test is msdf-atlas-gen's: a point well outside the bounds must
    /// measure outside (negative) to its nearest edge.
    /// </summary>
    public void OrientLikeTrueType()
    {
        var bounds = ComputeBounds();
        if (!bounds.IsValid)
            return;
        var outside = new Vector2D(bounds.Left - bounds.Width - 1, bounds.Bottom - bounds.Height - 1);
        var nearest = SignedDistance.Infinite;
        foreach (var contour in Contours)
        {
            foreach (var edge in contour.Edges)
            {
                var d = edge.SignedDistanceTo(outside, out _);
                if (SignedDistance.IsCloser(d, nearest))
                    nearest = d;
            }
        }

        if (nearest.Distance > 0)
            foreach (var contour in Contours)
                contour.Reverse();
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

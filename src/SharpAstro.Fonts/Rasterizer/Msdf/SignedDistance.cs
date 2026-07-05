namespace SharpAstro.Fonts.Rasterizer.Msdf;

/// <summary>
/// A signed distance plus the orthogonality tiebreak msdfgen carries alongside
/// it. When two edges are exactly equidistant from a point, the one whose
/// direction is more orthogonal to the point (smaller <see cref="Dot"/>) is the
/// truer nearest edge. <see cref="IsCloser"/> encodes msdfgen's <c>operator&lt;</c>.
/// </summary>
internal readonly struct SignedDistance(double distance, double dot)
{
    public readonly double Distance = distance;
    public readonly double Dot = dot;

    /// <summary>The "infinitely far" initial value a per-pixel minimum starts at.</summary>
    public static SignedDistance Infinite => new(double.MinValue, 0);

    /// <summary>True when <paramref name="a"/> is a closer (or equally-close but more-orthogonal) edge than <paramref name="b"/>.</summary>
    public static bool IsCloser(SignedDistance a, SignedDistance b)
    {
        var da = Math.Abs(a.Distance);
        var db = Math.Abs(b.Distance);
        return da < db || (da == db && a.Dot < b.Dot);
    }
}

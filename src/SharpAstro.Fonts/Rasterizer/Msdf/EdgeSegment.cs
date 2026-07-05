namespace SharpAstro.Fonts.Rasterizer.Msdf;

/// <summary>
/// One outline segment of a glyph contour — a line, quadratic, or cubic Bézier —
/// carrying its msdfgen edge colour. The signed-distance math (closest point,
/// sign from the cross product, the orthogonality tiebreak, and the endpoint
/// pseudo-distance correction) is a faithful port of msdfgen so the generated
/// field matches the reference.
/// </summary>
internal abstract class EdgeSegment
{
    public EdgeColor Color = EdgeColor.White;

    /// <summary>The point on the segment at parameter <paramref name="t"/> ∈ [0,1].</summary>
    public abstract Vector2D Point(double t);

    /// <summary>The (un-normalized) tangent at <paramref name="t"/>.</summary>
    public abstract Vector2D Direction(double t);

    /// <summary>
    /// The signed distance from <paramref name="origin"/> to this segment, with
    /// the parameter of the closest point (which may fall outside [0,1] — see
    /// <see cref="DistanceToPseudoDistance"/>).
    /// </summary>
    public abstract SignedDistance SignedDistanceTo(Vector2D origin, out double param);

    /// <summary>Split into three equal-parameter sub-segments (used by the single-corner "teardrop" colouring case).</summary>
    public abstract void SplitInThirds(out EdgeSegment a, out EdgeSegment b, out EdgeSegment c);

    /// <summary>Expand the axis-aligned ink box to include this segment (endpoints + interior extrema).</summary>
    public abstract void ExtendBounds(ref double left, ref double bottom, ref double right, ref double top);

    /// <summary>
    /// Add this segment's contribution to the nonzero winding number of a point:
    /// count crossings of the rightward horizontal ray at <paramref name="y"/>
    /// that lie strictly right of <paramref name="xOrigin"/>, signed by whether
    /// the contour is moving up (+1) or down (−1) through the ray. Used only for
    /// the polarity safety net.
    /// </summary>
    public abstract void AddRayCrossings(double y, double xOrigin, ref int winding);

    /// <summary>
    /// Convert the closest-point signed distance into a pseudo-distance when the
    /// closest point is past an endpoint: substitute the perpendicular distance
    /// to the edge's extended line, removing the discontinuity at joins.
    /// </summary>
    public void DistanceToPseudoDistance(ref SignedDistance distance, Vector2D origin, double param)
    {
        if (param < 0)
        {
            var dir = Direction(0).Normalize();
            var aq = origin - Point(0);
            if (Vector2D.Dot(aq, dir) < 0)
            {
                var pseudo = Vector2D.Cross(aq, dir);
                if (Math.Abs(pseudo) <= Math.Abs(distance.Distance))
                    distance = new SignedDistance(pseudo, 0);
            }
        }
        else if (param > 1)
        {
            var dir = Direction(1).Normalize();
            var bq = origin - Point(1);
            if (Vector2D.Dot(bq, dir) > 0)
            {
                var pseudo = Vector2D.Cross(bq, dir);
                if (Math.Abs(pseudo) <= Math.Abs(distance.Distance))
                    distance = new SignedDistance(pseudo, 0);
            }
        }
    }

    protected static int Sign(double n) => n > 0 ? 1 : n < 0 ? -1 : 0;

    /// <summary>Net signed crossings of the simple quadratic/linear roots for a horizontal ray, shared by the segments.</summary>
    protected void CountRoots(ReadOnlySpan<double> roots, int count, double y, double xOrigin, ref int winding)
    {
        for (var i = 0; i < count; i++)
        {
            var t = roots[i];
            if (t < 0 || t > 1)
                continue;
            if (Point(t).X <= xOrigin)
                continue;
            var dyAtT = Direction(t).Y;
            if (dyAtT > 0)
                winding++;
            else if (dyAtT < 0)
                winding--;
        }
    }
}

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

    /// <summary>
    /// Net signed ray crossings for a curve segment, counted per <em>y-monotone piece</em>
    /// (pieces split at the interior roots of dY/dt) with the same half-open interval rule the
    /// linear segment uses: a piece traversing y from <c>ya</c> to <c>yb</c> crosses the ray iff
    /// <c>y ∈ [min, max)</c> including the piece's LOW end and excluding its HIGH end, signed by
    /// the traversal direction. This makes shared joints count exactly once and tangent touches
    /// net zero — counting raw polynomial roots with t ∈ [0,1] inclusive double-counts a ray
    /// through a segment joint (fonts split curves at extrema, so round glyphs are full of
    /// them), inverting the winding for the rest of the scanline; ErrorCorrect then bakes that
    /// inversion into the field as a one-row stripe of phantom ink (the "defective o/c/g/b"
    /// dashes). The crossing parameter is located by bisection on the monotone piece, which is
    /// immune to root-solver edge cases at t≈0/1.
    /// <paramref name="y0"/>/<paramref name="y1"/> are the segment's exact endpoint Y values
    /// (bit-identical to the adjacent segments' shared endpoints, keeping the half-open
    /// boundaries consistent across joints).
    /// </summary>
    protected void CountMonotoneCrossings(double y0, double y1, ReadOnlySpan<double> interiorExtrema, int extremaCount,
        double y, double xOrigin, ref int winding)
    {
        Span<double> ts = stackalloc double[4];
        Span<double> ys = stackalloc double[4];
        var m = 0;
        ts[m] = 0; ys[m++] = y0;
        // collect interior dY/dt roots in (0,1), sorted (a quadratic solve may return them unordered)
        for (var i = 0; i < extremaCount; i++)
        {
            var t = interiorExtrema[i];
            if (t <= 0 || t >= 1) continue;
            var j = m++;
            while (j > 1 && ts[j - 1] > t) { ts[j] = ts[j - 1]; ys[j] = ys[j - 1]; j--; }
            ts[j] = t; ys[j] = Point(t).Y;
        }
        ts[m] = 1; ys[m++] = y1;

        for (var i = 0; i + 1 < m; i++)
        {
            double ya = ys[i], yb = ys[i + 1];
            int sign;
            if (ya <= y && y < yb) sign = 1;        // ascending piece: [ya, yb)
            else if (yb <= y && y < ya) sign = -1;  // descending piece: [yb, ya)
            else continue;

            // bisect the monotone piece for the crossing parameter
            double lo = ts[i], hi = ts[i + 1];
            var ascending = sign > 0;
            for (var it = 0; it < 40; it++)
            {
                var mid = 0.5 * (lo + hi);
                if (Point(mid).Y <= y == ascending) lo = mid;
                else hi = mid;
            }
            if (Point(0.5 * (lo + hi)).X > xOrigin)
                winding += sign;
        }
    }
}

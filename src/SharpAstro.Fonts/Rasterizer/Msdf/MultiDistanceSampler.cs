namespace SharpAstro.Fonts.Rasterizer.Msdf;

/// <summary>
/// msdfgen's per-channel perpendicular-distance selector. Naïvely converting the
/// nearest edge to a pseudo-distance notches the field at convex vertices (the
/// extended edge line passes closer than the vertex itself). This guards the
/// perpendicular substitution with the bisector test at each endpoint — using
/// the previous/next edge directions — so the perpendicular is only used where
/// it is the genuine nearest feature.
/// </summary>
internal struct PerChannelSelector()
{
    private SignedDistance _minTrue = SignedDistance.Infinite;
    private double _minNegPerp = double.NegativeInfinity;
    private double _minPosPerp = double.PositiveInfinity;
    private EdgeSegment? _nearEdge = null;
    private double _nearParam = 0;

    public void AddTrueDistance(EdgeSegment edge, SignedDistance distance, double param)
    {
        if (SignedDistance.IsCloser(distance, _minTrue))
        {
            _minTrue = distance;
            _nearEdge = edge;
            _nearParam = param;
        }
    }

    public void AddPerpendicularDistance(double d)
    {
        if (d <= 0 && d > _minNegPerp)
            _minNegPerp = d;
        if (d >= 0 && d < _minPosPerp)
            _minPosPerp = d;
    }

    /// <summary>Merge another contour's selector into this one, keeping the closer true edge and tighter perpendiculars.</summary>
    public void Merge(in PerChannelSelector other)
    {
        if (SignedDistance.IsCloser(other._minTrue, _minTrue))
        {
            _minTrue = other._minTrue;
            _nearEdge = other._nearEdge;
            _nearParam = other._nearParam;
        }

        if (other._minNegPerp > _minNegPerp)
            _minNegPerp = other._minNegPerp;
        if (other._minPosPerp < _minPosPerp)
            _minPosPerp = other._minPosPerp;
    }

    public readonly double Compute(Vector2D p)
    {
        var min = _minTrue.Distance < 0 ? _minNegPerp : _minPosPerp;
        if (_nearEdge is not null)
        {
            var sd = _minTrue;
            _nearEdge.DistanceToPseudoDistance(ref sd, p, _nearParam);
            if (Math.Abs(sd.Distance) < Math.Abs(min))
                min = sd.Distance;
        }

        return min;
    }
}

/// <summary>
/// Accumulates the three colour channels (and a true-distance channel for MTSDF
/// alpha) over a glyph's edges at one sample point, then reconstructs the four
/// signed distances. Mirrors msdfgen's <c>MultiAndTrueDistanceSelector</c>.
/// </summary>
internal struct MultiDistanceSampler()
{
    private PerChannelSelector _r = new();
    private PerChannelSelector _g = new();
    private PerChannelSelector _b = new();
    private SignedDistance _trueDistance = SignedDistance.Infinite;

    public void AddEdge(EdgeSegment edge, Vector2D p)
    {
        var distance = edge.SignedDistanceTo(p, out var param);

        if ((edge.Color & EdgeColor.Red) != 0)
            _r.AddTrueDistance(edge, distance, param);
        if ((edge.Color & EdgeColor.Green) != 0)
            _g.AddTrueDistance(edge, distance, param);
        if ((edge.Color & EdgeColor.Blue) != 0)
            _b.AddTrueDistance(edge, distance, param);
        if (SignedDistance.IsCloser(distance, _trueDistance))
            _trueDistance = distance;

        var ap = p - edge.PA;
        var bp = p - edge.PB;

        // Bisector tests: only consider the start/end perpendicular when the point is on the outer side of the
        // vertex bisector, so the perpendicular never cuts across a convex corner.
        var add = Vector2D.Dot(ap, edge.BisectorA);
        var bdd = -Vector2D.Dot(bp, edge.BisectorB);

        if (add > 0)
        {
            var pd = distance.Distance;
            if (GetPerpendicularDistance(ref pd, ap, -edge.DirA))
            {
                pd = -pd;
                AddPerp(edge.Color, pd);
            }
        }

        if (bdd > 0)
        {
            var pd = distance.Distance;
            if (GetPerpendicularDistance(ref pd, bp, edge.DirB))
                AddPerp(edge.Color, pd);
        }
    }

    public readonly (float R, float G, float B, float A) Resolve(Vector2D p, double invRange)
    {
        return (
            (float)(_r.Compute(p) * invRange + 0.5),
            (float)(_g.Compute(p) * invRange + 0.5),
            (float)(_b.Compute(p) * invRange + 0.5),
            (float)(_trueDistance.Distance * invRange + 0.5));
    }

    /// <summary>The reconstructed (median) signed distance — the scalar the contour combiner resolves contours by.</summary>
    public readonly double MedianDistance(Vector2D p)
    {
        var r = _r.Compute(p);
        var g = _g.Compute(p);
        var b = _b.Compute(p);
        return Math.Max(Math.Min(r, g), Math.Min(Math.Max(r, g), b));
    }

    /// <summary>Merge another contour's per-channel selectors (and the true-distance channel) into this one.</summary>
    public void Merge(in MultiDistanceSampler other)
    {
        _r.Merge(other._r);
        _g.Merge(other._g);
        _b.Merge(other._b);
        if (SignedDistance.IsCloser(other._trueDistance, _trueDistance))
            _trueDistance = other._trueDistance;
    }

    private void AddPerp(EdgeColor color, double pd)
    {
        if ((color & EdgeColor.Red) != 0)
            _r.AddPerpendicularDistance(pd);
        if ((color & EdgeColor.Green) != 0)
            _g.AddPerpendicularDistance(pd);
        if ((color & EdgeColor.Blue) != 0)
            _b.AddPerpendicularDistance(pd);
    }

    private static bool GetPerpendicularDistance(ref double distance, Vector2D ep, Vector2D edgeDir)
    {
        var ts = Vector2D.Dot(ep, edgeDir);
        if (ts > 0)
        {
            var perpendicular = Vector2D.Cross(ep, edgeDir);
            if (Math.Abs(perpendicular) < Math.Abs(distance))
            {
                distance = perpendicular;
                return true;
            }
        }

        return false;
    }
}

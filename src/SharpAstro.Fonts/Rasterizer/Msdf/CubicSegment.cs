namespace SharpAstro.Fonts.Rasterizer.Msdf;

/// <summary>
/// A cubic Bézier edge (the CFF / Type2 outline primitive used by OpenType-CFF
/// fonts). The closest point has no closed form, so the signed distance uses
/// msdfgen's Newton-refined multi-start search.
/// </summary>
internal sealed class CubicSegment(Vector2D p0, Vector2D p1, Vector2D p2, Vector2D p3) : EdgeSegment
{
    private const int SearchStarts = 4;
    private const int SearchSteps = 4;

    private readonly Vector2D _p0 = p0;
    private readonly Vector2D _p1 = p1;
    private readonly Vector2D _p2 = p2;
    private readonly Vector2D _p3 = p3;

    public override Vector2D Point(double t)
    {
        var p12 = Vector2D.Mix(_p1, _p2, t);
        return Vector2D.Mix(
            Vector2D.Mix(Vector2D.Mix(_p0, _p1, t), p12, t),
            Vector2D.Mix(p12, Vector2D.Mix(_p2, _p3, t), t),
            t);
    }

    public override Vector2D Direction(double t)
    {
        var tangent = Vector2D.Mix(Vector2D.Mix(_p1 - _p0, _p2 - _p1, t), Vector2D.Mix(_p2 - _p1, _p3 - _p2, t), t);
        if (tangent.X == 0 && tangent.Y == 0)
        {
            if (t == 0) return _p2 - _p0;
            if (t == 1) return _p3 - _p1;
        }

        return tangent;
    }

    public override SignedDistance SignedDistanceTo(Vector2D origin, out double param)
    {
        var qa = _p0 - origin;
        var ab = _p1 - _p0;
        var br = _p2 - _p1 - ab;
        var as_ = (_p3 - _p2) - (_p2 - _p1) - br;

        var epDir = Direction(0);
        var minDistance = Vector2D.NonZeroSign(Vector2D.Cross(epDir, qa)) * qa.Length;
        param = -Vector2D.Dot(qa, epDir) / Vector2D.Dot(epDir, epDir);
        {
            epDir = Direction(1);
            var distance = (_p3 - origin).Length;
            if (distance < Math.Abs(minDistance))
            {
                minDistance = Vector2D.NonZeroSign(Vector2D.Cross(epDir, _p3 - origin)) * distance;
                param = 1 + Vector2D.Dot(origin - _p3, epDir) / Vector2D.Dot(epDir, epDir);
            }
        }

        for (var i = 0; i <= SearchStarts; i++)
        {
            var t = (double)i / SearchStarts;
            var qe = qa + 3 * t * ab + 3 * t * t * br + t * t * t * as_;
            for (var step = 0; step < SearchSteps; step++)
            {
                var d1 = 3 * ab + 6 * t * br + 3 * t * t * as_;
                var d2 = 6 * br + 6 * t * as_;
                t -= Vector2D.Dot(qe, d1) / (Vector2D.Dot(d1, d1) + Vector2D.Dot(qe, d2));
                if (t <= 0 || t >= 1)
                    break;
                qe = qa + 3 * t * ab + 3 * t * t * br + t * t * t * as_;
            }

            if (t > 0 && t < 1)
            {
                var distance = qe.Length;
                if (distance < Math.Abs(minDistance))
                {
                    var d1 = 3 * ab + 6 * t * br + 3 * t * t * as_;
                    minDistance = Vector2D.NonZeroSign(Vector2D.Cross(d1, qe)) * distance;
                    param = t;
                }
            }
        }

        if (param >= 0 && param <= 1)
            return new SignedDistance(minDistance, 0);
        if (param < 0.5)
            return new SignedDistance(minDistance, Math.Abs(Vector2D.Dot(Direction(0).Normalize(), qa.Normalize())));
        return new SignedDistance(minDistance, Math.Abs(Vector2D.Dot(Direction(1).Normalize(), (_p3 - origin).Normalize())));
    }

    public override void SplitInThirds(out EdgeSegment a, out EdgeSegment b, out EdgeSegment c)
    {
        a = new CubicSegment(
            _p0,
            (_p0.X == _p1.X && _p0.Y == _p1.Y) ? _p0 : Vector2D.Mix(_p0, _p1, 1.0 / 3.0),
            Vector2D.Mix(Vector2D.Mix(_p0, _p1, 1.0 / 3.0), Vector2D.Mix(_p1, _p2, 1.0 / 3.0), 1.0 / 3.0),
            Point(1.0 / 3.0)) { Color = Color };
        b = new CubicSegment(
            Point(1.0 / 3.0),
            Vector2D.Mix(
                Vector2D.Mix(Vector2D.Mix(_p0, _p1, 1.0 / 3.0), Vector2D.Mix(_p1, _p2, 1.0 / 3.0), 1.0 / 3.0),
                Vector2D.Mix(Vector2D.Mix(_p1, _p2, 1.0 / 3.0), Vector2D.Mix(_p2, _p3, 1.0 / 3.0), 1.0 / 3.0),
                2.0 / 3.0),
            Vector2D.Mix(
                Vector2D.Mix(Vector2D.Mix(_p0, _p1, 2.0 / 3.0), Vector2D.Mix(_p1, _p2, 2.0 / 3.0), 2.0 / 3.0),
                Vector2D.Mix(Vector2D.Mix(_p1, _p2, 2.0 / 3.0), Vector2D.Mix(_p2, _p3, 2.0 / 3.0), 2.0 / 3.0),
                1.0 / 3.0),
            Point(2.0 / 3.0)) { Color = Color };
        c = new CubicSegment(
            Point(2.0 / 3.0),
            Vector2D.Mix(Vector2D.Mix(_p1, _p2, 2.0 / 3.0), Vector2D.Mix(_p2, _p3, 2.0 / 3.0), 2.0 / 3.0),
            (_p2.X == _p3.X && _p2.Y == _p3.Y) ? _p3 : Vector2D.Mix(_p2, _p3, 2.0 / 3.0),
            _p3) { Color = Color };
    }

    public override void ExtendBounds(ref double left, ref double bottom, ref double right, ref double top)
    {
        LinearSegment.Include(_p0, ref left, ref bottom, ref right, ref top);
        LinearSegment.Include(_p3, ref left, ref bottom, ref right, ref top);
        var a0 = _p1 - _p0;
        var a1 = 2 * (_p2 - _p1 - a0);
        var a2 = _p3 - 3 * _p2 + 3 * _p1 - _p0;
        Span<double> t = stackalloc double[2];
        var n = EquationSolver.SolveQuadratic(t, a2.X, a1.X, a0.X);
        for (var i = 0; i < n; i++)
            if (t[i] > 0 && t[i] < 1)
                LinearSegment.Include(Point(t[i]), ref left, ref bottom, ref right, ref top);
        n = EquationSolver.SolveQuadratic(t, a2.Y, a1.Y, a0.Y);
        for (var i = 0; i < n; i++)
            if (t[i] > 0 && t[i] < 1)
                LinearSegment.Include(Point(t[i]), ref left, ref bottom, ref right, ref top);
    }

    public override void AddRayCrossings(double y, double xOrigin, ref int winding)
    {
        // Up to two interior y-extrema where dY/dt = 3c3·t² + 2c2·t + c1 (Y components) = 0.
        var c3 = _p3 - 3 * _p2 + 3 * _p1 - _p0;
        var c2 = 3 * (_p2 - 2 * _p1 + _p0);
        var c1 = 3 * (_p1 - _p0);
        Span<double> ext = stackalloc double[2];
        var n = EquationSolver.SolveQuadratic(ext, 3 * c3.Y, 2 * c2.Y, c1.Y);
        if (n < 0)
            n = 0;
        CountMonotoneCrossings(_p0.Y, _p3.Y, ext, n, y, xOrigin, ref winding);
    }
}

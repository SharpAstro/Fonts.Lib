namespace SharpAstro.Fonts.Rasterizer.Msdf;

/// <summary>A quadratic Bézier edge (the TrueType <c>glyf</c> outline primitive).</summary>
internal sealed class QuadraticSegment : EdgeSegment
{
    private readonly Vector2D _p0;
    private readonly Vector2D _p1;
    private readonly Vector2D _p2;

    public QuadraticSegment(Vector2D p0, Vector2D p1, Vector2D p2)
    {
        // A control point coincident with an endpoint makes the curve degenerate; nudge it to the midpoint
        // (matching msdfgen) so the direction/distance math stays well-defined.
        if ((p1.X == p0.X && p1.Y == p0.Y) || (p1.X == p2.X && p1.Y == p2.Y))
            p1 = Vector2D.Mix(p0, p2, 0.5);
        _p0 = p0;
        _p1 = p1;
        _p2 = p2;
    }

    public override Vector2D Point(double t) =>
        Vector2D.Mix(Vector2D.Mix(_p0, _p1, t), Vector2D.Mix(_p1, _p2, t), t);

    public override Vector2D Direction(double t)
    {
        var tangent = Vector2D.Mix(_p1 - _p0, _p2 - _p1, t);
        if (tangent.X == 0 && tangent.Y == 0)
            return _p2 - _p0;
        return tangent;
    }

    public override SignedDistance SignedDistanceTo(Vector2D origin, out double param)
    {
        var qa = _p0 - origin;
        var ab = _p1 - _p0;
        var br = _p2 - _p1 - ab;
        var a = Vector2D.Dot(br, br);
        var b = 3 * Vector2D.Dot(ab, br);
        var c = 2 * Vector2D.Dot(ab, ab) + Vector2D.Dot(qa, br);
        var d = Vector2D.Dot(qa, ab);
        Span<double> t = stackalloc double[3];
        var solutions = EquationSolver.SolveCubic(t, a, b, c, d);

        var epDir = Direction(0);
        var minDistance = Vector2D.NonZeroSign(Vector2D.Cross(epDir, qa)) * qa.Length; // distance from A
        param = -Vector2D.Dot(qa, epDir) / Vector2D.Dot(epDir, epDir);
        {
            epDir = Direction(1);
            var distance = (_p2 - origin).Length; // distance from B
            if (distance < Math.Abs(minDistance))
            {
                minDistance = Vector2D.NonZeroSign(Vector2D.Cross(epDir, _p2 - origin)) * distance;
                param = Vector2D.Dot(origin - _p1, epDir) / Vector2D.Dot(epDir, epDir);
            }
        }

        for (var i = 0; i < solutions; i++)
        {
            if (t[i] > 0 && t[i] < 1)
            {
                var qe = qa + 2 * t[i] * ab + t[i] * t[i] * br; // point(t) - origin
                var distance = qe.Length;
                if (distance <= Math.Abs(minDistance))
                {
                    minDistance = Vector2D.NonZeroSign(Vector2D.Cross(ab + t[i] * br, qe)) * distance;
                    param = t[i];
                }
            }
        }

        if (param >= 0 && param <= 1)
            return new SignedDistance(minDistance, 0);
        if (param < 0.5)
            return new SignedDistance(minDistance, Math.Abs(Vector2D.Dot(Direction(0).Normalize(), qa.Normalize())));
        return new SignedDistance(minDistance, Math.Abs(Vector2D.Dot(Direction(1).Normalize(), (_p2 - origin).Normalize())));
    }

    public override void SplitInThirds(out EdgeSegment a, out EdgeSegment b, out EdgeSegment c)
    {
        a = new QuadraticSegment(_p0, Vector2D.Mix(_p0, _p1, 1.0 / 3.0), Point(1.0 / 3.0)) { Color = Color };
        b = new QuadraticSegment(
            Point(1.0 / 3.0),
            Vector2D.Mix(Vector2D.Mix(_p0, _p1, 5.0 / 9.0), Vector2D.Mix(_p1, _p2, 4.0 / 9.0), 0.5),
            Point(2.0 / 3.0)) { Color = Color };
        c = new QuadraticSegment(Point(2.0 / 3.0), Vector2D.Mix(_p1, _p2, 2.0 / 3.0), _p2) { Color = Color };
    }

    public override void ExtendBounds(ref double left, ref double bottom, ref double right, ref double top)
    {
        LinearSegment.Include(_p0, ref left, ref bottom, ref right, ref top);
        LinearSegment.Include(_p2, ref left, ref bottom, ref right, ref top);
        var bot = (_p1 - _p0) - (_p2 - _p1);
        if (bot.X != 0)
        {
            var t = (_p1.X - _p0.X) / bot.X;
            if (t > 0 && t < 1)
                LinearSegment.Include(Point(t), ref left, ref bottom, ref right, ref top);
        }

        if (bot.Y != 0)
        {
            var t = (_p1.Y - _p0.Y) / bot.Y;
            if (t > 0 && t < 1)
                LinearSegment.Include(Point(t), ref left, ref bottom, ref right, ref top);
        }
    }

    public override void AddRayCrossings(double y, double xOrigin, ref int winding)
    {
        var ab = _p1 - _p0;
        var br = _p2 - _p1 - ab;
        Span<double> t = stackalloc double[2];
        var n = EquationSolver.SolveQuadratic(t, br.Y, 2 * ab.Y, _p0.Y - y);
        if (n < 0)
            n = 0;
        CountRoots(t, n, y, xOrigin, ref winding);
    }
}

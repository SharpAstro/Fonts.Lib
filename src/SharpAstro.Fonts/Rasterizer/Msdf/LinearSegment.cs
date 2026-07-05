namespace SharpAstro.Fonts.Rasterizer.Msdf;

/// <summary>A straight edge between two points.</summary>
internal sealed class LinearSegment(Vector2D p0, Vector2D p1) : EdgeSegment
{
    private readonly Vector2D _p0 = p0;
    private readonly Vector2D _p1 = p1;

    public override Vector2D Point(double t) => Vector2D.Mix(_p0, _p1, t);

    public override Vector2D Direction(double t) => _p1 - _p0;

    public override SignedDistance SignedDistanceTo(Vector2D origin, out double param)
    {
        var aq = origin - _p0;
        var ab = _p1 - _p0;
        param = Vector2D.Dot(aq, ab) / Vector2D.Dot(ab, ab);
        var eq = (param > 0.5 ? _p1 : _p0) - origin;
        var endpointDistance = eq.Length;
        if (param > 0 && param < 1)
        {
            var orthoDistance = Vector2D.Dot(ab.Orthonormal(false), aq);
            if (Math.Abs(orthoDistance) < endpointDistance)
                return new SignedDistance(orthoDistance, 0);
        }

        return new SignedDistance(
            Vector2D.NonZeroSign(Vector2D.Cross(aq, ab)) * endpointDistance,
            Math.Abs(Vector2D.Dot(ab.Normalize(), eq.Normalize())));
    }

    public override void SplitInThirds(out EdgeSegment a, out EdgeSegment b, out EdgeSegment c)
    {
        a = new LinearSegment(_p0, Point(1.0 / 3.0)) { Color = Color };
        b = new LinearSegment(Point(1.0 / 3.0), Point(2.0 / 3.0)) { Color = Color };
        c = new LinearSegment(Point(2.0 / 3.0), _p1) { Color = Color };
    }

    public override void ExtendBounds(ref double left, ref double bottom, ref double right, ref double top)
    {
        Include(_p0, ref left, ref bottom, ref right, ref top);
        Include(_p1, ref left, ref bottom, ref right, ref top);
    }

    public override void AddRayCrossings(double y, double xOrigin, ref int winding)
    {
        if ((y >= _p0.Y && y < _p1.Y) || (y >= _p1.Y && y < _p0.Y))
        {
            var t = (y - _p0.Y) / (_p1.Y - _p0.Y);
            if (Vector2D.Mix(_p0, _p1, t).X > xOrigin)
                winding += Sign(_p1.Y - _p0.Y);
        }
    }

    internal static void Include(Vector2D p, ref double left, ref double bottom, ref double right, ref double top)
    {
        if (p.X < left) left = p.X;
        if (p.Y < bottom) bottom = p.Y;
        if (p.X > right) right = p.X;
        if (p.Y > top) top = p.Y;
    }
}

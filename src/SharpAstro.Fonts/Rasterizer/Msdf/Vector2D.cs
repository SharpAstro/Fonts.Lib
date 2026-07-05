// Multi-channel signed distance field generation, ported from msdfgen
// (Viktor Chlumsky, MIT) via the managed port in SUIsei (Steven Blom, MIT).
// The distance math is kept in double precision to match the reference
// generator; it is adapted here to SharpAstro.Fonts' IGlyphSink outline model.

namespace SharpAstro.Fonts.Rasterizer.Msdf;

/// <summary>
/// A double-precision 2D vector / point. The MTSDF math runs in <c>double</c>
/// throughout (matching msdfgen) for sign stability and corner accuracy; the
/// field is quantized to bytes only at the very end. Mirrors the small set of
/// operations msdfgen's <c>Vector2</c> exposes (dot, cross, normalize,
/// orthonormal).
/// </summary>
internal readonly struct Vector2D(double x, double y)
{
    public readonly double X = x;
    public readonly double Y = y;

    public static Vector2D operator +(Vector2D a, Vector2D b) => new(a.X + b.X, a.Y + b.Y);
    public static Vector2D operator -(Vector2D a, Vector2D b) => new(a.X - b.X, a.Y - b.Y);
    public static Vector2D operator -(Vector2D a) => new(-a.X, -a.Y);
    public static Vector2D operator *(Vector2D a, double s) => new(a.X * s, a.Y * s);
    public static Vector2D operator *(double s, Vector2D a) => new(a.X * s, a.Y * s);

    public double Length => Math.Sqrt(X * X + Y * Y);

    public static double Dot(Vector2D a, Vector2D b) => a.X * b.X + a.Y * b.Y;

    /// <summary>The 2D cross product (z of the 3D cross) — <c>a.x*b.y - a.y*b.x</c>.</summary>
    public static double Cross(Vector2D a, Vector2D b) => a.X * b.Y - a.Y * b.X;

    /// <summary>Linear blend, <c>a</c> at t=0 and <c>b</c> at t=1.</summary>
    public static Vector2D Mix(Vector2D a, Vector2D b, double t) => new(a.X + t * (b.X - a.X), a.Y + t * (b.Y - a.Y));

    /// <summary>Unit vector. A zero vector returns (0, !allowZero), matching msdfgen's degenerate convention.</summary>
    public Vector2D Normalize(bool allowZero = false)
    {
        var len = Length;
        if (len == 0)
            return new Vector2D(0, allowZero ? 0 : 1);
        return new Vector2D(X / len, Y / len);
    }

    /// <summary>
    /// The unit normal. <paramref name="polarity"/> true returns the left
    /// normal (-y, x), false the right (y, -x). Matches msdfgen's
    /// <c>getOrthonormal</c> (used by the linear-segment orthogonal-distance test).
    /// </summary>
    public Vector2D Orthonormal(bool polarity)
    {
        var len = Length;
        if (len == 0)
            return new Vector2D(0, 0);
        return polarity ? new Vector2D(-Y / len, X / len) : new Vector2D(Y / len, -X / len);
    }

    public static int NonZeroSign(double n) => n > 0 ? 1 : -1;
}

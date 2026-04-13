namespace SharpAstro.Fonts.Hinting;

/// <summary>
/// F26.6 fixed-point helpers — TrueType's native pixel coordinate format.
/// Bottom 6 bits are fractional (1 unit = 1/64 pixel), upper 26 bits integer.
///
/// <para>Stored as <see cref="int"/>; conversions are static methods rather
/// than a wrapper struct to keep call sites obvious.</para>
/// </summary>
internal static class F26Dot6
{
    /// <summary>1.0 in F26.6 = 64.</summary>
    public const int One = 64;

    public static int FromFloat(float v) => (int)MathF.Round(v * 64f);
    public static float ToFloat(int v) => v / 64f;
    public static int FromInt(int v) => v << 6;
    public static int ToIntFloor(int v) => v >> 6;
    public static int ToIntRound(int v) => (v + 32) >> 6;
    public static int ToIntCeil(int v) => (v + 63) >> 6;

    public static int Mul(int a, int b)
    {
        // 26.6 × 26.6 / 64. Use long to avoid overflow on intermediate.
        var prod = (long)a * b;
        return (int)((prod + 32) >> 6);
    }

    public static int Div(int a, int b)
    {
        if (b == 0) return a < 0 ? int.MinValue : int.MaxValue;
        var num = ((long)a) << 6;
        return (int)((num + (b >> 1)) / b);
    }
}

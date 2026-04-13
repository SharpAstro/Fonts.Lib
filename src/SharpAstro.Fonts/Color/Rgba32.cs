namespace SharpAstro.Fonts.Color;

/// <summary>
/// Packed 32-bit RGBA color. Byte layout matches <c>byte[]</c> emitted by
/// <see cref="ColorBitmap"/>: R, G, B, A.
/// </summary>
public readonly record struct Rgba32(byte R, byte G, byte B, byte A)
{
    public static readonly Rgba32 Transparent = new(0, 0, 0, 0);
    public static readonly Rgba32 Black = new(0, 0, 0, 255);

    /// <summary>Premultiply the color's alpha by <paramref name="multiplier"/> in [0,1].</summary>
    public Rgba32 WithMultipliedAlpha(float multiplier)
        => new(R, G, B, (byte)Math.Clamp((int)MathF.Round(A * multiplier), 0, 255));

    /// <summary>Linear interpolation between two colors.</summary>
    public static Rgba32 Lerp(Rgba32 a, Rgba32 b, float t)
    {
        if (t <= 0) return a;
        if (t >= 1) return b;
        return new Rgba32(
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t),
            (byte)(a.A + (b.A - a.A) * t));
    }
}

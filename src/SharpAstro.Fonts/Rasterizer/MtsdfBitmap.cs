namespace SharpAstro.Fonts.Rasterizer;

/// <summary>
/// Multi-channel signed distance field bitmap of a single glyph. Four bytes per
/// pixel (R, G, B, A), row-major, top-down. R/G/B carry the per-channel signed
/// pseudo-distance that keeps corners sharp; A carries the plain true signed
/// distance (the channel reserved for outline / glow / weight effects). In every
/// channel 128 (0.5) is exactly on the glyph edge, values above 128 are inside,
/// and values below are outside — the fragment shader reconstructs the edge from
/// <c>median(r, g, b)</c> and can read A independently.
///
/// <para>The distance encoding matches <see cref="SdfBitmap"/>: a signed distance
/// of ±<c>Spread</c> pixels maps to the full [0, 1] byte range, so the A channel
/// is a drop-in for the single-channel field and the same shader smoothing math
/// applies.</para>
/// </summary>
public sealed class MtsdfBitmap
{
    /// <summary>RGBA data, row-major, top-down, four bytes per pixel.</summary>
    public byte[] Rgba { get; }
    public int Width { get; }
    public int Height { get; }
    /// <summary>Pixels right of the pen X to the left edge of the bitmap (includes spread padding).</summary>
    public int Left { get; }
    /// <summary>Pixels above the baseline to the top edge of the bitmap (includes spread padding).</summary>
    public int Top { get; }
    /// <summary>The spread (in pixels) used during rasterization.</summary>
    public float Spread { get; }

    public MtsdfBitmap(byte[] rgba, int width, int height, int left, int top, float spread)
    {
        Rgba = rgba;
        Width = width;
        Height = height;
        Left = left;
        Top = top;
        Spread = spread;
    }

    public static readonly MtsdfBitmap Empty = new([], 0, 0, 0, 0, 0f);

    public bool IsEmpty => Width == 0 || Height == 0;
}

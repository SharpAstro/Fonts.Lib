namespace SharpAstro.Fonts.Rasterizer;

/// <summary>
/// Signed distance field bitmap of a single glyph. Each byte encodes a
/// distance value in [0, 255] where 128 (0.5) is exactly on the glyph edge,
/// values above 128 are inside the glyph, and values below 128 are outside.
/// </summary>
public sealed class SdfBitmap
{
    /// <summary>Single-channel SDF data, row-major, one byte per pixel.</summary>
    public byte[] Alpha { get; }
    public int Width { get; }
    public int Height { get; }
    /// <summary>Pixels right of the pen X to the left edge of the bitmap (includes spread padding).</summary>
    public int Left { get; }
    /// <summary>Pixels above the baseline to the top edge of the bitmap (includes spread padding).</summary>
    public int Top { get; }
    /// <summary>The spread (in pixels) used during rasterization.</summary>
    public float Spread { get; }

    public SdfBitmap(byte[] alpha, int width, int height, int left, int top, float spread)
    {
        Alpha = alpha;
        Width = width;
        Height = height;
        Left = left;
        Top = top;
        Spread = spread;
    }

    public static readonly SdfBitmap Empty = new([], 0, 0, 0, 0, 0f);

    public bool IsEmpty => Width == 0 || Height == 0;
}

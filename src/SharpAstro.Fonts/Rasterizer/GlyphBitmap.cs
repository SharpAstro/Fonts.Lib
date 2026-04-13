namespace SharpAstro.Fonts.Rasterizer;

/// <summary>
/// Grayscale 8-bit alpha-coverage bitmap produced by the rasterizer.
///
/// <para>Coordinate convention: pixels are row-major, top-down. The bitmap's
/// position relative to the glyph "pen" is given by <see cref="Left"/>
/// (pixels right of pen X) and <see cref="Top"/> (pixels above baseline,
/// matching FreeType's <c>bitmap_top</c> convention).</para>
///
/// <para>Immutable; safe to share across threads.</para>
/// </summary>
public sealed class GlyphBitmap
{
    public byte[] Alpha { get; }
    public int Width { get; }
    public int Height { get; }
    /// <summary>Pixels right of the pen X to the left edge of the bitmap.</summary>
    public int Left { get; }
    /// <summary>Pixels above the baseline to the top edge of the bitmap.</summary>
    public int Top { get; }

    public GlyphBitmap(byte[] alpha, int width, int height, int left, int top)
    {
        Alpha = alpha;
        Width = width;
        Height = height;
        Left = left;
        Top = top;
    }

    public static readonly GlyphBitmap Empty = new([], 0, 0, 0, 0);

    public bool IsEmpty => Width == 0 || Height == 0;
}

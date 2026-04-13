namespace SharpAstro.Fonts.Color;

/// <summary>
/// 32-bit RGBA bitmap output for color glyphs (COLR / CBDT / sbix).
/// Same <see cref="Left"/> / <see cref="Top"/> conventions as
/// <see cref="Rasterizer.GlyphBitmap"/>: pixels right of pen, pixels above
/// baseline.
///
/// <para>Immutable. Pixels stored row-major as R,G,B,A (length = 4*W*H).</para>
/// </summary>
public sealed class ColorBitmap
{
    public byte[] Pixels { get; }
    public int Width { get; }
    public int Height { get; }
    public int Left { get; }
    public int Top { get; }

    public ColorBitmap(byte[] pixels, int width, int height, int left, int top)
    {
        Pixels = pixels;
        Width = width;
        Height = height;
        Left = left;
        Top = top;
    }

    public static readonly ColorBitmap Empty = new([], 0, 0, 0, 0);
    public bool IsEmpty => Width == 0 || Height == 0;

    /// <summary>
    /// Source-over blend a single pixel with non-premultiplied color and a
    /// per-pixel coverage in [0,255]. Convenience for paint renderers.
    /// </summary>
    internal void BlendOver(int x, int y, Rgba32 color, byte coverage)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height || coverage == 0)
            return;

        var srcA = (color.A * coverage + 127) / 255;
        if (srcA == 0) return;

        var i = (y * Width + x) * 4;
        var dstR = Pixels[i];
        var dstG = Pixels[i + 1];
        var dstB = Pixels[i + 2];
        var dstA = Pixels[i + 3];

        // Standard SVG/CSS source-over with non-premultiplied alpha.
        var inv = 255 - srcA;
        Pixels[i]     = (byte)((color.R * srcA + dstR * inv + 127) / 255);
        Pixels[i + 1] = (byte)((color.G * srcA + dstG * inv + 127) / 255);
        Pixels[i + 2] = (byte)((color.B * srcA + dstB * inv + 127) / 255);
        Pixels[i + 3] = (byte)Math.Min(255, srcA + (dstA * inv + 127) / 255);
    }
}

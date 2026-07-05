using SharpAstro.Fonts.Tables.Cbdt;
using SharpAstro.Fonts.Tables.Cblc;
using SharpAstro.Png;

namespace SharpAstro.Fonts.Color;

/// <summary>
/// Decode a CBDT PNG-bitmap glyph into a <see cref="ColorBitmap"/>.
///
/// <para>If the requested PPEM differs from the strike PPEM, the image is
/// resampled with bilinear filtering so the output matches the caller's
/// expected size. Stateless / per-call — safe for concurrent use.</para>
/// </summary>
internal static class CbdtRenderer
{
    public static ColorBitmap? TryRender(OpenTypeFont font, uint glyphId, float pixelsPerEm)
    {
        if (font.Cblc is null || font.Cbdt is null) return null;
        var strike = font.Cblc.PickStrike(pixelsPerEm);
        if (strike is null) return null;

        var img = font.Cbdt.GetImage(strike, glyphId);
        if (img is null) return null;

        // Decode PNG -> RGBA8 via the dedicated SharpAstro.Png codec. It handles
        // color types 0/2/4/6 at 8/16-bit and rejects indexed / interlaced PNGs
        // (throws) -> the catch below turns any such glyph into a null (skip).
        // CBDT color glyphs are RGBA8 (color type 6) in practice.
        byte[] srcPixels;
        int srcW, srcH;
        try
        {
            var decoded = PngReader.Decode(img.Value.Png.Span);
            if (decoded.Width <= 0 || decoded.Height <= 0) return null;
            var rgba = ToRgba8(decoded);
            if (rgba is null) return null; // unsupported PNG variant (e.g. indexed-color)
            srcPixels = rgba;
            srcW = decoded.Width;
            srcH = decoded.Height;
        }
        catch
        {
            return null;
        }

        // Scale so srcH (≈ strike.PpemY) → pixelsPerEm.
        var scale = pixelsPerEm / strike.PpemY;
        var dstW = Math.Max(1, (int)MathF.Round(srcW * scale));
        var dstH = Math.Max(1, (int)MathF.Round(srcH * scale));

        byte[] pixels;
        if (dstW == srcW && dstH == srcH)
        {
            pixels = srcPixels;
        }
        else
        {
            pixels = ResampleBilinear(srcPixels, srcW, srcH, dstW, dstH);
        }

        // Bearings: provided by the format (for 17/18) or by const metrics
        // (format 19 / index format 2). Scale to requested PPEM.
        var left = (int)MathF.Round(img.Value.BearingX * scale);
        var top = (int)MathF.Round(img.Value.BearingY * scale);
        return new ColorBitmap(pixels, dstW, dstH, left, top);
    }

    private static byte[] ResampleBilinear(byte[] src, int sw, int sh, int dw, int dh)
    {
        var dst = new byte[dw * dh * 4];
        var fx = (sw - 1f) / Math.Max(1, dw - 1);
        var fy = (sh - 1f) / Math.Max(1, dh - 1);

        for (var y = 0; y < dh; y++)
        {
            var sy = y * fy;
            var y0 = (int)sy;
            var y1 = Math.Min(sh - 1, y0 + 1);
            var ty = sy - y0;
            for (var x = 0; x < dw; x++)
            {
                var sx = x * fx;
                var x0 = (int)sx;
                var x1 = Math.Min(sw - 1, x0 + 1);
                var tx = sx - x0;

                var i00 = (y0 * sw + x0) * 4;
                var i10 = (y0 * sw + x1) * 4;
                var i01 = (y1 * sw + x0) * 4;
                var i11 = (y1 * sw + x1) * 4;
                var di = (y * dw + x) * 4;
                for (var c = 0; c < 4; c++)
                {
                    var c00 = src[i00 + c];
                    var c10 = src[i10 + c];
                    var c01 = src[i01 + c];
                    var c11 = src[i11 + c];
                    var top = c00 + (c10 - c00) * tx;
                    var bot = c01 + (c11 - c01) * tx;
                    dst[di + c] = (byte)Math.Clamp((int)(top + (bot - top) * ty + 0.5f), 0, 255);
                }
            }
        }
        return dst;
    }

    /// <summary>
    /// Normalize a decoded <see cref="PngImage"/> into tightly-packed 8-bit RGBA
    /// (row-major, 4 bytes/pixel) — the layout the rest of the renderer (and
    /// <see cref="ResampleBilinear"/>) expects. Mirrors what the stb port's
    /// <c>ColorComponents.RedGreenBlueAlpha</c> produced from any source format.
    /// 16-bit samples are truncated to their high byte (PNG stores 16-bit
    /// big-endian, so the byte at the sample offset already IS the high byte).
    /// Returns null for color types this renderer can't expand (indexed).
    /// </summary>
    private static byte[]? ToRgba8(PngImage img)
    {
        var w = img.Width;
        var h = img.Height;
        var src = img.Pixels;
        var spp = img.SamplesPerPixel;
        var step = img.BitDepth == 16 ? 2 : 1; // bytes per sample (high byte first for 16-bit)
        var rowBytes = w * spp * step;
        var dst = new byte[w * h * 4];

        for (var y = 0; y < h; y++)
        {
            var srcRow = y * rowBytes;
            var dstRow = y * w * 4;
            for (var x = 0; x < w; x++)
            {
                var s = srcRow + x * spp * step;
                byte r, g, b, a;
                switch (img.ColorType)
                {
                    case 0: // grayscale
                        r = g = b = src[s];
                        a = 255;
                        break;
                    case 2: // RGB
                        r = src[s];
                        g = src[s + step];
                        b = src[s + 2 * step];
                        a = 255;
                        break;
                    case 3: // indexed (palette): src[s] is an index into Palette (+ optional PaletteAlpha)
                        var pal = img.Palette;
                        if (pal is null) return null;
                        var idx = src[s];
                        var pi = idx * 3;
                        if (pi + 2 >= pal.Length) return null;
                        r = pal[pi];
                        g = pal[pi + 1];
                        b = pal[pi + 2];
                        a = img.PaletteAlpha is { } pa && idx < pa.Length ? pa[idx] : (byte)255;
                        break;
                    case 4: // grayscale + alpha
                        r = g = b = src[s];
                        a = src[s + step];
                        break;
                    case 6: // RGBA
                        r = src[s];
                        g = src[s + step];
                        b = src[s + 2 * step];
                        a = src[s + 3 * step];
                        break;
                    default: // indexed (3) etc. — not expanded here
                        return null;
                }
                var d = dstRow + x * 4;
                dst[d] = r;
                dst[d + 1] = g;
                dst[d + 2] = b;
                dst[d + 3] = a;
            }
        }
        return dst;
    }
}

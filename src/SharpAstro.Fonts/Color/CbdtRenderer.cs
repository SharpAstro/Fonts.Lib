using SharpAstro.Fonts.Tables.Cbdt;
using SharpAstro.Fonts.Tables.Cblc;
using StbImageSharp;

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

        // Decode PNG → RGBA.
        ImageResult decoded;
        try
        {
            decoded = ImageResult.FromMemory(img.Value.Png.ToArray(),
                ColorComponents.RedGreenBlueAlpha);
        }
        catch
        {
            return null;
        }
        if (decoded.Width <= 0 || decoded.Height <= 0) return null;

        // Strike pixel dimensions used by the font's bearings.
        var srcW = decoded.Width;
        var srcH = decoded.Height;

        // Scale so srcH (≈ strike.PpemY) → pixelsPerEm.
        var scale = pixelsPerEm / strike.PpemY;
        var dstW = Math.Max(1, (int)MathF.Round(srcW * scale));
        var dstH = Math.Max(1, (int)MathF.Round(srcH * scale));

        byte[] pixels;
        if (dstW == srcW && dstH == srcH)
        {
            pixels = decoded.Data;
        }
        else
        {
            pixels = ResampleBilinear(decoded.Data, srcW, srcH, dstW, dstH);
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
}

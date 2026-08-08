using SharpAstro.Fonts.Shaping;

namespace SharpAstro.Fonts.Web;

/// <summary>One rendered line: a tightly-packed RGBA buffer ready for <c>putImageData</c>,
/// plus the numbers the page reports next to it.</summary>
internal sealed record RenderedRun(
    byte[] Rgba, int Width, int Height, int GlyphCount, double ShapeMs, double RasterMs,
    int NotdefCount);

/// <summary>Lays a string out through <c>SharpAstro.Fonts.Shaping</c> and rasterizes it into an
/// RGBA buffer — the "ours" half of the side-by-side. The browser half needs none of this: it
/// gets the same file through <c>@font-face</c> and does the identical job internally.</summary>
internal static class GlyphRunRenderer
{
    /// <summary>Black-on-white, matching what a browser paints by default, so the two halves are
    /// comparable without colour-managing anything.</summary>
    private const int Padding = 8;

    /// <param name="now">Browser clock in milliseconds. <see cref="System.Diagnostics.Stopwatch"/>
    /// reports a flat 0.000 ms here for work that demonstrably takes longer, so the caller supplies
    /// <c>performance.now()</c> instead.</param>
    public static RenderedRun Render(
        OpenTypeFont font, ShapingFont shapingFont, string text, float pixelsPerEm, bool hinted,
        Func<double> now)
    {
        var upem = font.UnitsPerEm;
        var scale = pixelsPerEm / upem;

        // Real vertical metrics where the face has them; hhea is optional in the type system, and
        // a face without it still has to land on a sensible baseline rather than at y=0.
        var ascent = font.Hhea?.Ascender ?? (short)(upem * 4 / 5);
        var descent = font.Hhea?.Descender ?? (short)(-upem / 5);

        var height = (int)Math.Ceiling((ascent - descent) * scale) + Padding * 2;
        var baselineY = (int)Math.Ceiling(ascent * scale) + Padding;

        // --- shape -------------------------------------------------------------------------
        var t0 = now();
        var notdef = 0;
        var placed = new List<(uint GlyphId, float X, float Y)>();
        var penX = (float)Padding;

        // ScriptItemizer splits arbitrary typed text into single-script runs by itself. RTL runs
        // come back from the shaper already in VISUAL order, so each run blits left-to-right; a
        // paragraph mixing both directions would need BidiScriptItemizer to order the runs too.
        var runs = ScriptItemizer.Itemize(text);
        var buffer = new ShapeBuffer();
        var hmtx = font.Hmtx;

        foreach (var run in runs)
        {
            buffer.Clear();
            buffer.Direction = run.Direction;
            buffer.AddText(text.AsSpan(run.Start, run.Length));
            Shaper.Shape(shapingFont, buffer, run.Script);

            for (var i = 0; i < buffer.Length; i++)
            {
                var gid = buffer.GlyphIds[i];
                if (gid == 0) notdef++;
                placed.Add((gid,
                    penX + buffer.XOffsets[i] * scale,
                    -buffer.YOffsets[i] * scale));

                var advance = (hmtx?.GetAdvanceWidth(gid) ?? 0) + buffer.XAdvanceDeltas[i];
                penX += advance * scale;
            }
        }

        var shapeMs = now() - t0;

        var width = (int)Math.Ceiling(penX) + Padding;
        if (width < 1) width = 1;
        if (height < 1) height = 1;

        // --- rasterize ---------------------------------------------------------------------
        var t1 = now();
        var rgba = new byte[width * height * 4];
        Array.Fill(rgba, (byte)255);           // opaque white ground

        foreach (var (gid, x, y) in placed)
        {
            var bmp = hinted
                ? font.RenderGlyphHinted(gid, pixelsPerEm)
                : font.RenderGlyph(gid, pixelsPerEm);

            if (bmp.IsEmpty) continue;

            var originX = (int)MathF.Round(x) + bmp.Left;
            var originY = baselineY - bmp.Top + (int)MathF.Round(y);

            for (var row = 0; row < bmp.Height; row++)
            {
                var destY = originY + row;
                if (destY < 0 || destY >= height) continue;

                for (var col = 0; col < bmp.Width; col++)
                {
                    var destX = originX + col;
                    if (destX < 0 || destX >= width) continue;

                    var coverage = bmp.Alpha[row * bmp.Width + col];
                    if (coverage == 0) continue;

                    var o = (destY * width + destX) * 4;
                    // Black ink over whatever is there; glyphs can overlap (marks, kerned pairs),
                    // so compose rather than assign.
                    var ink = (byte)(255 - coverage);
                    if (ink < rgba[o]) { rgba[o] = ink; rgba[o + 1] = ink; rgba[o + 2] = ink; }
                }
            }
        }

        return new RenderedRun(rgba, width, height, placed.Count, shapeMs, now() - t1, notdef);
    }
}

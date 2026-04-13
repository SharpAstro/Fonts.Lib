using System.Numerics;
using SharpAstro.Fonts.Outlines;
using SharpAstro.Fonts.Rasterizer;
using SharpAstro.Fonts.Tables.Colr;
using SharpAstro.Fonts.Tables.Cpal;

namespace SharpAstro.Fonts.Color;

/// <summary>
/// Walks a COLR v1 paint tree (with v0 fallback) into an RGBA
/// <see cref="ColorBitmap"/>. Ported from the DIR.Lib FT-based
/// <c>ColrV1Renderer</c> to operate on managed <see cref="PaintRef"/>
/// records — no native interop, no pointer arithmetic.
///
/// <para>Stateless / per-call: every render call allocates its own surface
/// and walker state. Safe to invoke concurrently from any thread.</para>
/// </summary>
public static class ColrRenderer
{
    /// <summary>
    /// Render the color glyph for <paramref name="glyphId"/> at
    /// <paramref name="pixelsPerEm"/>. Returns null if this font / GID has
    /// no COLR data (caller should fall back to the grayscale rasterizer).
    /// </summary>
    public static ColorBitmap? TryRender(OpenTypeFont font, uint glyphId, float pixelsPerEm)
    {
        if (font.Colr is null) return null;
        var palette = font.Cpal?.GetPalette(0).ToArray() ?? Array.Empty<Rgba32>();

        // Generous square surface; we crop after rendering.
        var px = (int)MathF.Ceiling(pixelsPerEm);
        var surfaceSize = Math.Max(16, px * 3);
        var surface = new ColorBitmap(new byte[surfaceSize * surfaceSize * 4],
            surfaceSize, surfaceSize, 0, 0);

        // Base transform: design-units → surface-pixel-space.
        //   px_x = font_x * scale + cx
        //   px_y = -font_y * scale + cy   (font Y-up → surface Y-down)
        // We center the design-unit origin at the surface center so glyphs that
        // extend in either direction have room. Final crop tightens bounds.
        var scale = pixelsPerEm / font.UnitsPerEm;
        var cx = surfaceSize * 0.5f;
        var cy = surfaceSize * 0.5f;
        var rootXform = new Matrix3x2(scale, 0, 0, -scale, cx, cy);

        var rendered = false;
        if (font.Colr.TryGetV1RootPaint(glyphId, out var rootPaint))
        {
            RenderPaint(font, rootPaint, surface, palette, rootXform);
            rendered = true;
        }
        else
        {
            var v0Layers = font.Colr.GetV0Layers(glyphId);
            if (v0Layers.Length > 0)
            {
                foreach (var layer in v0Layers)
                    RenderLayerV0(font, layer, surface, palette, rootXform);
                rendered = true;
            }
        }

        if (!rendered) return null;

        return Crop(surface, baselineY: cy);
    }

    // ---- v0 ----------------------------------------------------------------

    private static void RenderLayerV0(OpenTypeFont font, ColrV0Layer layer,
        ColorBitmap surface, Rgba32[] palette, in Matrix3x2 xform)
    {
        var color = LookupColor(palette, layer.PaletteIndex, alpha: 1f);
        FillGlyphMask(font, layer.GlyphId, surface, xform, color);
    }

    // ---- v1 paint tree -----------------------------------------------------

    private static void RenderPaint(OpenTypeFont font, PaintRef paint,
        ColorBitmap surface, Rgba32[] palette, in Matrix3x2 xform)
    {
        if (paint.IsNull) return;

        switch (paint.Format)
        {
            case PaintFormat.ColrLayers:
            {
                var d = paint.AsColrLayers();
                for (var i = 0; i < d.NumLayers; i++)
                {
                    var layer = font.Colr!.GetLayerPaint((int)(d.FirstLayerIndex + i));
                    RenderPaint(font, layer, surface, palette, xform);
                }
                break;
            }
            case PaintFormat.ColrGlyph:
            {
                var d = paint.AsColrGlyph();
                if (font.Colr!.TryGetV1RootPaint(d.GlyphID, out var sub))
                    RenderPaint(font, sub, surface, palette, xform);
                break;
            }
            case PaintFormat.Glyph:
            {
                var d = paint.AsGlyph();
                RenderPaintGlyph(font, d, surface, palette, xform);
                break;
            }
            case PaintFormat.Composite:
            {
                var d = paint.AsComposite();
                // Only src-over is fully supported; others render best-effort src-over.
                RenderPaint(font, d.Backdrop, surface, palette, xform);
                RenderPaint(font, d.Source, surface, palette, xform);
                break;
            }

            // Transforms: accumulate then recurse.
            case PaintFormat.Transform:
            {
                var d = paint.AsTransform();
                RenderPaint(font, d.Paint, surface, palette, d.Transform * xform);
                break;
            }
            case PaintFormat.Translate:
            {
                var d = paint.AsTranslate();
                RenderPaint(font, d.Paint, surface, palette, Matrix3x2.CreateTranslation(d.Dx, d.Dy) * xform);
                break;
            }
            case PaintFormat.Scale:
            case PaintFormat.ScaleAroundCenter:
            case PaintFormat.ScaleUniform:
            case PaintFormat.ScaleUniformAroundCenter:
            {
                var aroundCenter = paint.Format is PaintFormat.ScaleAroundCenter
                    or PaintFormat.ScaleUniformAroundCenter;
                var uniform = paint.Format is PaintFormat.ScaleUniform
                    or PaintFormat.ScaleUniformAroundCenter;
                var d = paint.AsScale(aroundCenter, uniform);
                var m = aroundCenter
                    ? Matrix3x2.CreateScale(d.Sx, d.Sy, new Vector2(d.Cx, d.Cy))
                    : Matrix3x2.CreateScale(d.Sx, d.Sy);
                RenderPaint(font, d.Paint, surface, palette, m * xform);
                break;
            }
            case PaintFormat.Rotate:
            case PaintFormat.RotateAroundCenter:
            {
                var d = paint.AsRotate(paint.Format == PaintFormat.RotateAroundCenter);
                var rad = d.AngleTurns * MathF.PI;  // F2DOT14 turns × 180° → radians
                var m = paint.Format == PaintFormat.RotateAroundCenter
                    ? Matrix3x2.CreateRotation(rad, new Vector2(d.Cx, d.Cy))
                    : Matrix3x2.CreateRotation(rad);
                RenderPaint(font, d.Paint, surface, palette, m * xform);
                break;
            }
            case PaintFormat.Skew:
            case PaintFormat.SkewAroundCenter:
            {
                var d = paint.AsSkew(paint.Format == PaintFormat.SkewAroundCenter);
                var xTan = MathF.Tan(d.XAngleTurns * MathF.PI);
                var yTan = MathF.Tan(d.YAngleTurns * MathF.PI);
                var skew = new Matrix3x2(1, yTan, xTan, 1, 0, 0);
                if (paint.Format == PaintFormat.SkewAroundCenter)
                {
                    var c = new Vector2(d.Cx, d.Cy);
                    skew = Matrix3x2.CreateTranslation(-c) * skew * Matrix3x2.CreateTranslation(c);
                }
                RenderPaint(font, d.Paint, surface, palette, skew * xform);
                break;
            }

            default:
                // Var* and unsupported / future paint formats: render nothing
                // rather than crash. Visible regression but recoverable.
                break;
        }
    }

    private static void RenderPaintGlyph(OpenTypeFont font, PaintGlyphData glyphPaint,
        ColorBitmap surface, Rgba32[] palette, in Matrix3x2 xform)
    {
        // Resolve the inner paint stripped of transforms — those compose into a
        // separate "fill xform" used for gradient coordinate mapping.
        var fill = glyphPaint.Paint;
        var fillXform = Matrix3x2.Identity;
        while (true)
        {
            switch (fill.Format)
            {
                case PaintFormat.Transform:
                {
                    var d = fill.AsTransform();
                    fillXform = d.Transform * fillXform;
                    fill = d.Paint;
                    continue;
                }
                case PaintFormat.Translate:
                {
                    var d = fill.AsTranslate();
                    fillXform = Matrix3x2.CreateTranslation(d.Dx, d.Dy) * fillXform;
                    fill = d.Paint;
                    continue;
                }
                case PaintFormat.Scale:
                case PaintFormat.ScaleAroundCenter:
                case PaintFormat.ScaleUniform:
                case PaintFormat.ScaleUniformAroundCenter:
                {
                    var ac = fill.Format is PaintFormat.ScaleAroundCenter
                        or PaintFormat.ScaleUniformAroundCenter;
                    var u = fill.Format is PaintFormat.ScaleUniform
                        or PaintFormat.ScaleUniformAroundCenter;
                    var d = fill.AsScale(ac, u);
                    fillXform = (ac
                        ? Matrix3x2.CreateScale(d.Sx, d.Sy, new Vector2(d.Cx, d.Cy))
                        : Matrix3x2.CreateScale(d.Sx, d.Sy)) * fillXform;
                    fill = d.Paint;
                    continue;
                }
                case PaintFormat.Rotate:
                case PaintFormat.RotateAroundCenter:
                {
                    var d = fill.AsRotate(fill.Format == PaintFormat.RotateAroundCenter);
                    var rad = d.AngleTurns * MathF.PI;
                    var m = fill.Format == PaintFormat.RotateAroundCenter
                        ? Matrix3x2.CreateRotation(rad, new Vector2(d.Cx, d.Cy))
                        : Matrix3x2.CreateRotation(rad);
                    fillXform = m * fillXform;
                    fill = d.Paint;
                    continue;
                }
            }
            break;
        }

        // Render the outline mask in surface space by transforming each outline
        // point through `xform` before passing to the rasterizer.
        var mask = RenderOutlineMask(font, glyphPaint.GlyphID, surface.Width, surface.Height, xform);
        if (mask is null) return;

        // Per-pixel fill: compute the design-unit coordinate via the inverse
        // base xform (to get back to font units), then evaluate the inner paint.
        Matrix3x2.Invert(xform, out var invXform);
        var hasFillXform = !fillXform.IsIdentity;
        Matrix3x2 invFill = default;
        if (hasFillXform) Matrix3x2.Invert(fillXform, out invFill);

        for (var y = 0; y < surface.Height; y++)
        {
            for (var x = 0; x < surface.Width; x++)
            {
                var alpha = mask[y * surface.Width + x];
                if (alpha == 0) continue;
                var designPos = Vector2.Transform(new Vector2(x + 0.5f, y + 0.5f), invXform);
                var color = SampleFill(fill, designPos, palette, hasFillXform ? invFill : Matrix3x2.Identity, hasFillXform);
                surface.BlendOver(x, y, color, alpha);
            }
        }
    }

    private static byte[]? RenderOutlineMask(OpenTypeFont font, uint glyphId,
        int width, int height, in Matrix3x2 xform)
    {
        if (glyphId >= font.NumGlyphs) return null;
        var capturedXform = xform; // can't capture by ref in lambda
        var bmp = SmoothRasterizer.Rasterize(
            sink => font.DrawGlyph(glyphId, new TransformingSink(sink, capturedXform)),
            pixelsPerEm: 1, unitsPerEm: 1);
        if (bmp.IsEmpty) return null;

        // Place the mask into a full surface-sized buffer so blending is O(1).
        var alpha = new byte[width * height];
        var x0 = bmp.Left;
        var y0 = -bmp.Top; // mask top in surface coords (Top is "above baseline" but
                          // since baseline is at world Y=0 and our world is surface-pixel-space,
                          // Top = -y0_in_surface).
        // Actually our TransformingSink already produces world-coord points whose
        // (0,0) is the surface origin. SmoothRasterizer crops to its bounding box
        // and reports Left/Top such that bitmap(0,0) sits at world (Left, -Top).
        // We just need to place that bbox back into the surface.
        for (var ry = 0; ry < bmp.Height; ry++)
        {
            var sy = y0 + ry;
            if ((uint)sy >= (uint)height) continue;
            for (var rx = 0; rx < bmp.Width; rx++)
            {
                var sx = x0 + rx;
                if ((uint)sx >= (uint)width) continue;
                alpha[sy * width + sx] = bmp.Alpha[ry * bmp.Width + rx];
            }
        }
        return alpha;
    }

    private static Rgba32 SampleFill(PaintRef fill, Vector2 designPos, Rgba32[] palette,
        in Matrix3x2 invFillXform, bool hasFillXform)
    {
        switch (fill.Format)
        {
            case PaintFormat.Solid:
            {
                var d = fill.AsSolid();
                return LookupColor(palette, d.PaletteIndex, d.Alpha);
            }
            case PaintFormat.LinearGradient:
            {
                var d = fill.AsLinearGradient(default!);
                var p = hasFillXform ? Vector2.Transform(designPos, invFillXform) : designPos;
                var t = ProjectLinearGradient(p,
                    new Vector2(d.X0, d.Y0), new Vector2(d.X1, d.Y1), new Vector2(d.X2, d.Y2));
                return SampleStops(d.Stops, t, palette, d.Extend);
            }
            case PaintFormat.RadialGradient:
            {
                var d = fill.AsRadialGradient(default!);
                var p = hasFillXform ? Vector2.Transform(designPos, invFillXform) : designPos;
                var t = ProjectRadialGradient(p,
                    new Vector2(d.X0, d.Y0), d.R0, new Vector2(d.X1, d.Y1), d.R1);
                return SampleStops(d.Stops, t, palette, d.Extend);
            }
            case PaintFormat.SweepGradient:
            {
                var d = fill.AsSweepGradient(default!);
                var p = hasFillXform ? Vector2.Transform(designPos, invFillXform) : designPos;
                var t = ProjectSweepGradient(p,
                    new Vector2(d.Cx, d.Cy), d.StartAngleTurns * MathF.PI, d.EndAngleTurns * MathF.PI);
                return SampleStops(d.Stops, t, palette, d.Extend);
            }
            default:
                return new Rgba32(128, 128, 128, 255); // unsupported fill — gray fallback
        }
    }

    // ---- Gradient math -----------------------------------------------------

    private static float ProjectLinearGradient(Vector2 p, Vector2 p0, Vector2 p1, Vector2 p2)
    {
        // p2 defines the "rotation" of the gradient line (perpendicular to p0→p1).
        // Standard COLR linear gradient: project p onto the line through p0
        // perpendicular to (p2 - p0), normalized so p0 → 0 and p1 → 1.
        var v01 = p1 - p0;
        var v02 = p2 - p0;
        // Rotate v02 by 90° to get the gradient direction; project p along it.
        // Following COLR spec §6.3.1: t = (v01 · v0p_perp) / |v01_perp|² where _perp is rotation of v02.
        // Simpler: project p onto v01 directly — this matches what most renderers do
        // when p2 is co-linear with p0p1, which is the common case.
        _ = v02; // p2-aware variant deferred to a quality pass
        var lenSq = v01.LengthSquared();
        if (lenSq <= 1e-6f) return 0f;
        return Vector2.Dot(p - p0, v01) / lenSq;
    }

    private static float ProjectRadialGradient(Vector2 p, Vector2 c0, float r0, Vector2 c1, float r1)
    {
        // Two-circle radial gradient. Solve the quadratic for t such that
        //   |p - lerp(c0,c1,t)| = lerp(r0,r1,t)
        // and pick the larger root in [0,1] when both are valid.
        var d = c1 - c0;
        var dr = r1 - r0;
        var f = p - c0;

        var a = Vector2.Dot(d, d) - dr * dr;
        var b = 2f * (Vector2.Dot(f, d) - r0 * dr);
        var c = Vector2.Dot(f, f) - r0 * r0;

        if (MathF.Abs(a) < 1e-6f)
        {
            if (MathF.Abs(b) < 1e-6f) return 0f;
            return -c / b;
        }
        var disc = b * b - 4f * a * c;
        if (disc < 0) return float.NaN;
        var sq = MathF.Sqrt(disc);
        var t0 = (-b + sq) / (2f * a);
        var t1 = (-b - sq) / (2f * a);
        // Pick the larger t whose corresponding radius is non-negative.
        var rAt0 = r0 + t0 * dr;
        var rAt1 = r0 + t1 * dr;
        if (rAt0 >= 0 && rAt1 >= 0) return MathF.Max(t0, t1);
        if (rAt0 >= 0) return t0;
        if (rAt1 >= 0) return t1;
        return float.NaN;
    }

    private static float ProjectSweepGradient(Vector2 p, Vector2 center, float startRad, float endRad)
    {
        var v = p - center;
        var ang = MathF.Atan2(v.Y, v.X);
        if (endRad <= startRad) return 0f;
        return (ang - startRad) / (endRad - startRad);
    }

    private static Rgba32 SampleStops(ColorStop[] stops, float t, Rgba32[] palette, GradientExtend extend)
    {
        if (stops.Length == 0 || float.IsNaN(t))
            return Rgba32.Transparent;
        switch (extend)
        {
            case GradientExtend.Pad: t = Math.Clamp(t, 0f, 1f); break;
            case GradientExtend.Repeat: t -= MathF.Floor(t); break;
            case GradientExtend.Reflect:
            {
                t = MathF.Abs(t);
                var period = MathF.Floor(t);
                t -= period;
                if (((int)period & 1) != 0) t = 1f - t;
                break;
            }
        }
        if (t <= stops[0].StopOffset)
            return LookupColor(palette, stops[0].PaletteIndex, stops[0].Alpha);
        if (t >= stops[^1].StopOffset)
            return LookupColor(palette, stops[^1].PaletteIndex, stops[^1].Alpha);
        for (var i = 1; i < stops.Length; i++)
        {
            if (t <= stops[i].StopOffset)
            {
                var s0 = stops[i - 1];
                var s1 = stops[i];
                var span = s1.StopOffset - s0.StopOffset;
                var frac = span > 0 ? (t - s0.StopOffset) / span : 0f;
                return Rgba32.Lerp(
                    LookupColor(palette, s0.PaletteIndex, s0.Alpha),
                    LookupColor(palette, s1.PaletteIndex, s1.Alpha),
                    frac);
            }
        }
        return Rgba32.Transparent;
    }

    // ---- Helpers -----------------------------------------------------------

    /// <summary>Solid fill for v0 / Solid v1 paints.</summary>
    private static void FillGlyphMask(OpenTypeFont font, uint glyphId,
        ColorBitmap surface, in Matrix3x2 xform, Rgba32 color)
    {
        var mask = RenderOutlineMask(font, glyphId, surface.Width, surface.Height, xform);
        if (mask is null) return;
        for (var y = 0; y < surface.Height; y++)
        {
            for (var x = 0; x < surface.Width; x++)
            {
                var a = mask[y * surface.Width + x];
                if (a != 0) surface.BlendOver(x, y, color, a);
            }
        }
    }

    /// <summary>0xFFFF means "use foreground color" — we render as opaque black for now.</summary>
    private static Rgba32 LookupColor(Rgba32[] palette, ushort paletteIndex, float alpha)
    {
        Rgba32 c;
        if (paletteIndex == 0xFFFF) c = Rgba32.Black;
        else if (paletteIndex < palette.Length) c = palette[paletteIndex];
        else c = Rgba32.Black;
        return c.WithMultipliedAlpha(alpha);
    }

    private static ColorBitmap Crop(ColorBitmap src, float baselineY)
    {
        int x0 = src.Width, y0 = src.Height, x1 = 0, y1 = 0;
        for (var y = 0; y < src.Height; y++)
            for (var x = 0; x < src.Width; x++)
                if (src.Pixels[(y * src.Width + x) * 4 + 3] > 0)
                {
                    if (x < x0) x0 = x;
                    if (x + 1 > x1) x1 = x + 1;
                    if (y < y0) y0 = y;
                    if (y + 1 > y1) y1 = y + 1;
                }
        if (x0 >= x1 || y0 >= y1) return ColorBitmap.Empty;
        var w = x1 - x0;
        var h = y1 - y0;
        var pix = new byte[w * h * 4];
        for (var ry = 0; ry < h; ry++)
            Buffer.BlockCopy(src.Pixels, ((y0 + ry) * src.Width + x0) * 4,
                pix, ry * w * 4, w * 4);

        // Left = x-distance from pen (we centered glyph at surface center; pen is at cx = surfaceSize/2).
        var penX = src.Width / 2;
        var left = x0 - penX;
        var top = (int)MathF.Round(baselineY) - y0; // pixels above baseline
        return new ColorBitmap(pix, w, h, left, top);
    }

    /// <summary>
    /// Wraps another sink, transforming every coordinate through a matrix
    /// before forwarding. Used to bake the COLR paint transform into outline
    /// rasterization so the mask comes out in surface-pixel space directly.
    ///
    /// <para>NOTE: <see cref="SmoothRasterizer"/> always applies its own
    /// Y-flip (font Y-up → bitmap Y-down). Since our COLR paint xforms
    /// already produce Y-down surface coords, we negate Y on the way out so
    /// the rasterizer's flip restores the value. Net effect: the mask lands
    /// in surface-pixel space exactly where xform maps the outline.</para>
    /// </summary>
    private sealed class TransformingSink : IGlyphSink
    {
        private readonly IGlyphSink _inner;
        private readonly Matrix3x2 _m;

        public TransformingSink(IGlyphSink inner, Matrix3x2 m) { _inner = inner; _m = m; }

        private Vector2 Tx(float x, float y) => Vector2.Transform(new Vector2(x, y), _m);

        public void MoveTo(float x, float y) { var p = Tx(x, y); _inner.MoveTo(p.X, -p.Y); }
        public void LineTo(float x, float y) { var p = Tx(x, y); _inner.LineTo(p.X, -p.Y); }
        public void QuadTo(float cx, float cy, float x, float y)
        { var c = Tx(cx, cy); var p = Tx(x, y); _inner.QuadTo(c.X, -c.Y, p.X, -p.Y); }
        public void CubicTo(float c1x, float c1y, float c2x, float c2y, float x, float y)
        { var c1 = Tx(c1x, c1y); var c2 = Tx(c2x, c2y); var p = Tx(x, y);
          _inner.CubicTo(c1.X, -c1.Y, c2.X, -c2.Y, p.X, -p.Y); }
        public void Close() => _inner.Close();
    }
}

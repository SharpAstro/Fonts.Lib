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

        // Capture normalized coords for Var* paint evaluation.
        var normalizedCoords = font.NormalizedCoords;

        var rendered = false;
        if (font.Colr.TryGetV1RootPaint(glyphId, out var rootPaint))
        {
            RenderPaint(font, rootPaint, surface, palette, rootXform, normalizedCoords);
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
        ColorBitmap surface, Rgba32[] palette, in Matrix3x2 xform,
        ReadOnlySpan<float> normalizedCoords)
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
                    RenderPaint(font, layer, surface, palette, xform, normalizedCoords);
                }
                break;
            }
            case PaintFormat.ColrGlyph:
            {
                var d = paint.AsColrGlyph();
                if (font.Colr!.TryGetV1RootPaint(d.GlyphID, out var sub))
                    RenderPaint(font, sub, surface, palette, xform, normalizedCoords);
                break;
            }
            case PaintFormat.Glyph:
            {
                var d = paint.AsGlyph();
                RenderPaintGlyph(font, d, surface, palette, xform, normalizedCoords);
                break;
            }
            case PaintFormat.Composite:
            {
                var d = paint.AsComposite();
                RenderComposite(font, d, surface, palette, xform, normalizedCoords);
                break;
            }

            // Transforms: accumulate then recurse.
            case PaintFormat.Transform:
            {
                var d = paint.AsTransform();
                RenderPaint(font, d.Paint, surface, palette, d.Transform * xform, normalizedCoords);
                break;
            }
            case PaintFormat.VarTransform:
            {
                // VarTransform: 6 matrix fields variably adjusted; individual field deltas
                // are not applied here because the matrix is read via Fixed16.16 already.
                // We decode with the IVS-adjusted raw Fixed16.16 values for correctness.
                var (d, varBase) = paint.AsVarTransform();
                // Apply deltas to the matrix components (xx,yx,xy,yy,dx,dy = base+0..+5).
                var colr = font.Colr!;
                var m = new Matrix3x2(
                    d.Transform.M11 + colr.GetVarDelta(varBase + 0, normalizedCoords),
                    d.Transform.M12 + colr.GetVarDelta(varBase + 1, normalizedCoords),
                    d.Transform.M21 + colr.GetVarDelta(varBase + 2, normalizedCoords),
                    d.Transform.M22 + colr.GetVarDelta(varBase + 3, normalizedCoords),
                    d.Transform.M31 + colr.GetVarDelta(varBase + 4, normalizedCoords),
                    d.Transform.M32 + colr.GetVarDelta(varBase + 5, normalizedCoords));
                RenderPaint(font, d.Paint, surface, palette, m * xform, normalizedCoords);
                break;
            }
            case PaintFormat.Translate:
            {
                var d = paint.AsTranslate();
                RenderPaint(font, d.Paint, surface, palette,
                    Matrix3x2.CreateTranslation(d.Dx, d.Dy) * xform, normalizedCoords);
                break;
            }
            case PaintFormat.VarTranslate:
            {
                var (d, varBase) = paint.AsVarTranslate();
                var colr = font.Colr!;
                var dx = d.Dx + colr.GetVarDelta(varBase + 0, normalizedCoords);
                var dy = d.Dy + colr.GetVarDelta(varBase + 1, normalizedCoords);
                RenderPaint(font, d.Paint, surface, palette,
                    Matrix3x2.CreateTranslation(dx, dy) * xform, normalizedCoords);
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
                RenderPaint(font, d.Paint, surface, palette, m * xform, normalizedCoords);
                break;
            }
            case PaintFormat.VarScale:
            case PaintFormat.VarScaleAroundCenter:
            case PaintFormat.VarScaleUniform:
            case PaintFormat.VarScaleUniformAroundCenter:
            {
                var aroundCenter = paint.Format is PaintFormat.VarScaleAroundCenter
                    or PaintFormat.VarScaleUniformAroundCenter;
                var uniform = paint.Format is PaintFormat.VarScaleUniform
                    or PaintFormat.VarScaleUniformAroundCenter;
                var (d, varBase) = paint.AsVarScale(aroundCenter, uniform);
                var colr = font.Colr!;
                // For uniform: varBase+0 = scale; for non-uniform: +0=sx, +1=sy.
                // AroundCenter adds cx=next, cy=next+1.
                float sx, sy;
                uint nextVar;
                if (uniform)
                {
                    sx = sy = d.Sx + colr.GetVarDelta(varBase + 0, normalizedCoords);
                    nextVar = varBase + 1;
                }
                else
                {
                    sx = d.Sx + colr.GetVarDelta(varBase + 0, normalizedCoords);
                    sy = d.Sy + colr.GetVarDelta(varBase + 1, normalizedCoords);
                    nextVar = varBase + 2;
                }
                Matrix3x2 m;
                if (aroundCenter)
                {
                    var cx = d.Cx + colr.GetVarDelta(nextVar + 0, normalizedCoords);
                    var cy = d.Cy + colr.GetVarDelta(nextVar + 1, normalizedCoords);
                    m = Matrix3x2.CreateScale(sx, sy, new Vector2(cx, cy));
                }
                else
                {
                    m = Matrix3x2.CreateScale(sx, sy);
                }
                RenderPaint(font, d.Paint, surface, palette, m * xform, normalizedCoords);
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
                RenderPaint(font, d.Paint, surface, palette, m * xform, normalizedCoords);
                break;
            }
            case PaintFormat.VarRotate:
            case PaintFormat.VarRotateAroundCenter:
            {
                var isAround = paint.Format == PaintFormat.VarRotateAroundCenter;
                var (d, varBase) = paint.AsVarRotate(isAround);
                var colr = font.Colr!;
                var angleTurns = d.AngleTurns + colr.GetVarDelta(varBase + 0, normalizedCoords);
                var rad = angleTurns * MathF.PI;
                Matrix3x2 m;
                if (isAround)
                {
                    var cx = d.Cx + colr.GetVarDelta(varBase + 1, normalizedCoords);
                    var cy = d.Cy + colr.GetVarDelta(varBase + 2, normalizedCoords);
                    m = Matrix3x2.CreateRotation(rad, new Vector2(cx, cy));
                }
                else
                {
                    m = Matrix3x2.CreateRotation(rad);
                }
                RenderPaint(font, d.Paint, surface, palette, m * xform, normalizedCoords);
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
                RenderPaint(font, d.Paint, surface, palette, skew * xform, normalizedCoords);
                break;
            }
            case PaintFormat.VarSkew:
            case PaintFormat.VarSkewAroundCenter:
            {
                var isAround = paint.Format == PaintFormat.VarSkewAroundCenter;
                var (d, varBase) = paint.AsVarSkew(isAround);
                var colr = font.Colr!;
                var xAngle = d.XAngleTurns + colr.GetVarDelta(varBase + 0, normalizedCoords);
                var yAngle = d.YAngleTurns + colr.GetVarDelta(varBase + 1, normalizedCoords);
                var xTan = MathF.Tan(xAngle * MathF.PI);
                var yTan = MathF.Tan(yAngle * MathF.PI);
                var skew = new Matrix3x2(1, yTan, xTan, 1, 0, 0);
                if (isAround)
                {
                    var cx = d.Cx + colr.GetVarDelta(varBase + 2, normalizedCoords);
                    var cy = d.Cy + colr.GetVarDelta(varBase + 3, normalizedCoords);
                    var c = new Vector2(cx, cy);
                    skew = Matrix3x2.CreateTranslation(-c) * skew * Matrix3x2.CreateTranslation(c);
                }
                RenderPaint(font, d.Paint, surface, palette, skew * xform, normalizedCoords);
                break;
            }
            case PaintFormat.VarSolid:
            case PaintFormat.VarLinearGradient:
            case PaintFormat.VarRadialGradient:
            case PaintFormat.VarSweepGradient:
                // These Var* fill formats require gradient color-stop variation which
                // is driven by the fill evaluation path (SampleFill). Delegate to the
                // Glyph handler; per-pixel sampling will pick up the base values.
                // Full delta wiring for color-stop var is deferred — for now fall
                // through and treat as the non-Var equivalent via the default case.
                goto default;

            default:
                // Unsupported / future paint formats: render nothing rather than crash.
                // Visible gap but recoverable.
                break;
        }
    }

    private static void RenderPaintGlyph(OpenTypeFont font, PaintGlyphData glyphPaint,
        ColorBitmap surface, Rgba32[] palette, in Matrix3x2 xform,
        ReadOnlySpan<float> normalizedCoords)
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
                case PaintFormat.VarTransform:
                {
                    // Use base transform values for fill space; Var* deltas on transform
                    // components are small corrections and we use non-Var reader as fallback.
                    var d = fill.AsTransform();
                    fillXform = d.Transform * fillXform;
                    fill = d.Paint;
                    continue;
                }
                case PaintFormat.Translate:
                case PaintFormat.VarTranslate:
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
                case PaintFormat.VarScale:
                case PaintFormat.VarScaleAroundCenter:
                case PaintFormat.VarScaleUniform:
                case PaintFormat.VarScaleUniformAroundCenter:
                {
                    var ac = fill.Format is PaintFormat.ScaleAroundCenter
                        or PaintFormat.ScaleUniformAroundCenter
                        or PaintFormat.VarScaleAroundCenter
                        or PaintFormat.VarScaleUniformAroundCenter;
                    var u = fill.Format is PaintFormat.ScaleUniform
                        or PaintFormat.ScaleUniformAroundCenter
                        or PaintFormat.VarScaleUniform
                        or PaintFormat.VarScaleUniformAroundCenter;
                    var d = fill.AsScale(ac, u);
                    fillXform = (ac
                        ? Matrix3x2.CreateScale(d.Sx, d.Sy, new Vector2(d.Cx, d.Cy))
                        : Matrix3x2.CreateScale(d.Sx, d.Sy)) * fillXform;
                    fill = d.Paint;
                    continue;
                }
                case PaintFormat.Rotate:
                case PaintFormat.RotateAroundCenter:
                case PaintFormat.VarRotate:
                case PaintFormat.VarRotateAroundCenter:
                {
                    var ac = fill.Format is PaintFormat.RotateAroundCenter
                        or PaintFormat.VarRotateAroundCenter;
                    var d = fill.AsRotate(ac);
                    var rad = d.AngleTurns * MathF.PI;
                    var m = ac
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
        var maskResult = RenderOutlineMask(font, glyphPaint.GlyphID, surface.Width, surface.Height, xform);
        if (maskResult is not { } mask) return;

        // Per-pixel fill: compute the design-unit coordinate via the inverse
        // base xform (to get back to font units), then evaluate the inner paint.
        // Only iterate the mask's bounding box — O(M²) where M = mask size.
        Matrix3x2.Invert(xform, out var invXform);
        var hasFillXform = !fillXform.IsIdentity;
        Matrix3x2 invFill = default;
        if (hasFillXform) Matrix3x2.Invert(fillXform, out invFill);

        for (var my = 0; my < mask.MaskHeight; my++)
        {
            var sy = mask.Y0 + my;
            for (var mx = 0; mx < mask.MaskWidth; mx++)
            {
                var alpha = mask.Alpha[my * mask.MaskWidth + mx];
                if (alpha == 0) continue;
                var sx = mask.X0 + mx;
                var designPos = Vector2.Transform(new Vector2(sx + 0.5f, sy + 0.5f), invXform);
                var color = SampleFill(fill, designPos, palette, hasFillXform ? invFill : Matrix3x2.Identity, hasFillXform);
                surface.BlendOver(sx, sy, color, alpha);
            }
        }
    }

    /// <summary>
    /// Handle PaintComposite: render backdrop and source to separate off-screen
    /// surfaces, then composite them using the specified Porter-Duff mode into
    /// <paramref name="surface"/>. All standard Porter-Duff modes are supported.
    /// </summary>
    private static void RenderComposite(OpenTypeFont font, PaintCompositeData composite,
        ColorBitmap surface, Rgba32[] palette, in Matrix3x2 xform,
        ReadOnlySpan<float> normalizedCoords)
    {
        // Allocate two temporary surfaces the same size as the main surface.
        var w = surface.Width;
        var h = surface.Height;

        var backdropPixels = new byte[w * h * 4];
        var srcPixels = new byte[w * h * 4];

        var backdropSurface = new ColorBitmap(backdropPixels, w, h, 0, 0);
        var srcSurface      = new ColorBitmap(srcPixels,      w, h, 0, 0);

        RenderPaint(font, composite.Backdrop, backdropSurface, palette, xform, normalizedCoords);
        RenderPaint(font, composite.Source,   srcSurface,      palette, xform, normalizedCoords);

        // Composite srcSurface over backdropSurface into the main surface using the specified mode.
        var dst = surface.Pixels;
        for (var i = 0; i < w * h; i++)
        {
            var pi = i * 4;

            // Source (non-premultiplied)
            var sr = srcPixels[pi];
            var sg = srcPixels[pi + 1];
            var sb = srcPixels[pi + 2];
            var sa = srcPixels[pi + 3];

            // Backdrop / destination (non-premultiplied)
            var dr = backdropPixels[pi];
            var dg = backdropPixels[pi + 1];
            var db = backdropPixels[pi + 2];
            var da = backdropPixels[pi + 3];

            // Convert to premultiplied float for Porter-Duff arithmetic.
            var Src_r = sr * sa / 255f;
            var Src_g = sg * sa / 255f;
            var Src_b = sb * sa / 255f;
            var Src_a = sa / 255f;

            var Dst_r = dr * da / 255f;
            var Dst_g = dg * da / 255f;
            var Dst_b = db * da / 255f;
            var Dst_a = da / 255f;

            float Out_r, Out_g, Out_b, Out_a;

            switch (composite.Mode)
            {
                case CompositeMode.Clear:
                    Out_r = Out_g = Out_b = Out_a = 0f;
                    break;
                case CompositeMode.Src:
                    Out_r = Src_r; Out_g = Src_g; Out_b = Src_b; Out_a = Src_a;
                    break;
                case CompositeMode.Dest:
                    Out_r = Dst_r; Out_g = Dst_g; Out_b = Dst_b; Out_a = Dst_a;
                    break;
                default:
                case CompositeMode.SrcOver:
                    // Src over Dst: Src + Dst*(1-srcA)
                    Out_a = Src_a + Dst_a * (1f - Src_a);
                    Out_r = Src_r + Dst_r * (1f - Src_a);
                    Out_g = Src_g + Dst_g * (1f - Src_a);
                    Out_b = Src_b + Dst_b * (1f - Src_a);
                    break;
                case CompositeMode.DestOver:
                    // Dst over Src: Dst + Src*(1-dstA)
                    Out_a = Dst_a + Src_a * (1f - Dst_a);
                    Out_r = Dst_r + Src_r * (1f - Dst_a);
                    Out_g = Dst_g + Src_g * (1f - Dst_a);
                    Out_b = Dst_b + Src_b * (1f - Dst_a);
                    break;
                case CompositeMode.SrcIn:
                    // Result = Src * dstA
                    Out_a = Src_a * Dst_a;
                    Out_r = Src_r * Dst_a;
                    Out_g = Src_g * Dst_a;
                    Out_b = Src_b * Dst_a;
                    break;
                case CompositeMode.DestIn:
                    // Result = Dst * srcA
                    Out_a = Dst_a * Src_a;
                    Out_r = Dst_r * Src_a;
                    Out_g = Dst_g * Src_a;
                    Out_b = Dst_b * Src_a;
                    break;
                case CompositeMode.SrcOut:
                    // Result = Src * (1 - dstA)
                    Out_a = Src_a * (1f - Dst_a);
                    Out_r = Src_r * (1f - Dst_a);
                    Out_g = Src_g * (1f - Dst_a);
                    Out_b = Src_b * (1f - Dst_a);
                    break;
                case CompositeMode.DestOut:
                    // Result = Dst * (1 - srcA)
                    Out_a = Dst_a * (1f - Src_a);
                    Out_r = Dst_r * (1f - Src_a);
                    Out_g = Dst_g * (1f - Src_a);
                    Out_b = Dst_b * (1f - Src_a);
                    break;
                case CompositeMode.SrcAtop:
                    // Result = Src*dstA + Dst*(1-srcA)
                    Out_a = Dst_a;
                    Out_r = Src_r * Dst_a + Dst_r * (1f - Src_a);
                    Out_g = Src_g * Dst_a + Dst_g * (1f - Src_a);
                    Out_b = Src_b * Dst_a + Dst_b * (1f - Src_a);
                    break;
                case CompositeMode.DestAtop:
                    // Result = Dst*srcA + Src*(1-dstA)
                    Out_a = Src_a;
                    Out_r = Dst_r * Src_a + Src_r * (1f - Dst_a);
                    Out_g = Dst_g * Src_a + Src_g * (1f - Dst_a);
                    Out_b = Dst_b * Src_a + Src_b * (1f - Dst_a);
                    break;
                case CompositeMode.Xor:
                    // Result = Src*(1-dstA) + Dst*(1-srcA)
                    Out_a = Src_a * (1f - Dst_a) + Dst_a * (1f - Src_a);
                    Out_r = Src_r * (1f - Dst_a) + Dst_r * (1f - Src_a);
                    Out_g = Src_g * (1f - Dst_a) + Dst_g * (1f - Src_a);
                    Out_b = Src_b * (1f - Dst_a) + Dst_b * (1f - Src_a);
                    break;
                case CompositeMode.Plus:
                    // Additive: Src + Dst, clamped.
                    Out_a = Math.Min(1f, Src_a + Dst_a);
                    Out_r = Math.Min(Out_a > 0 ? Out_a : 1f, Src_r + Dst_r);
                    Out_g = Math.Min(Out_a > 0 ? Out_a : 1f, Src_g + Dst_g);
                    Out_b = Math.Min(Out_a > 0 ? Out_a : 1f, Src_b + Dst_b);
                    break;
                case CompositeMode.Screen:
                    // 1 - (1-Src)*(1-Dst) in premul: Src + Dst - Src*Dst
                    Out_a = Src_a + Dst_a - Src_a * Dst_a;
                    Out_r = Src_r + Dst_r - Src_r * Dst_r;
                    Out_g = Src_g + Dst_g - Src_g * Dst_g;
                    Out_b = Src_b + Dst_b - Src_b * Dst_b;
                    break;
                case CompositeMode.Overlay:
                    Out_a = Src_a + Dst_a * (1f - Src_a);
                    Out_r = HardLightChannel(Dst_r, Dst_a, Src_r, Src_a);
                    Out_g = HardLightChannel(Dst_g, Dst_a, Src_g, Src_a);
                    Out_b = HardLightChannel(Dst_b, Dst_a, Src_b, Src_a);
                    break;
                case CompositeMode.Darken:
                    Out_a = Src_a + Dst_a * (1f - Src_a);
                    Out_r = Math.Min(Src_r * Dst_a, Dst_r * Src_a) + Src_r * (1f - Dst_a) + Dst_r * (1f - Src_a);
                    Out_g = Math.Min(Src_g * Dst_a, Dst_g * Src_a) + Src_g * (1f - Dst_a) + Dst_g * (1f - Src_a);
                    Out_b = Math.Min(Src_b * Dst_a, Dst_b * Src_a) + Src_b * (1f - Dst_a) + Dst_b * (1f - Src_a);
                    break;
                case CompositeMode.Lighten:
                    Out_a = Src_a + Dst_a * (1f - Src_a);
                    Out_r = Math.Max(Src_r * Dst_a, Dst_r * Src_a) + Src_r * (1f - Dst_a) + Dst_r * (1f - Src_a);
                    Out_g = Math.Max(Src_g * Dst_a, Dst_g * Src_a) + Src_g * (1f - Dst_a) + Dst_g * (1f - Src_a);
                    Out_b = Math.Max(Src_b * Dst_a, Dst_b * Src_a) + Src_b * (1f - Dst_a) + Dst_b * (1f - Src_a);
                    break;
                case CompositeMode.ColorDodge:
                    Out_a = Src_a + Dst_a * (1f - Src_a);
                    Out_r = DodgeChannel(Src_r, Src_a, Dst_r, Dst_a);
                    Out_g = DodgeChannel(Src_g, Src_a, Dst_g, Dst_a);
                    Out_b = DodgeChannel(Src_b, Src_a, Dst_b, Dst_a);
                    break;
                case CompositeMode.ColorBurn:
                    Out_a = Src_a + Dst_a * (1f - Src_a);
                    Out_r = BurnChannel(Src_r, Src_a, Dst_r, Dst_a);
                    Out_g = BurnChannel(Src_g, Src_a, Dst_g, Dst_a);
                    Out_b = BurnChannel(Src_b, Src_a, Dst_b, Dst_a);
                    break;
                case CompositeMode.HardLight:
                    Out_a = Src_a + Dst_a * (1f - Src_a);
                    Out_r = HardLightChannel(Src_r, Src_a, Dst_r, Dst_a);
                    Out_g = HardLightChannel(Src_g, Src_a, Dst_g, Dst_a);
                    Out_b = HardLightChannel(Src_b, Src_a, Dst_b, Dst_a);
                    break;
                case CompositeMode.SoftLight:
                    Out_a = Src_a + Dst_a * (1f - Src_a);
                    Out_r = SoftLightChannel(Src_r, Src_a, Dst_r, Dst_a);
                    Out_g = SoftLightChannel(Src_g, Src_a, Dst_g, Dst_a);
                    Out_b = SoftLightChannel(Src_b, Src_a, Dst_b, Dst_a);
                    break;
                case CompositeMode.Difference:
                    Out_a = Src_a + Dst_a * (1f - Src_a);
                    Out_r = Src_r + Dst_r - 2f * Math.Min(Src_r * Dst_a, Dst_r * Src_a);
                    Out_g = Src_g + Dst_g - 2f * Math.Min(Src_g * Dst_a, Dst_g * Src_a);
                    Out_b = Src_b + Dst_b - 2f * Math.Min(Src_b * Dst_a, Dst_b * Src_a);
                    break;
                case CompositeMode.Exclusion:
                    Out_a = Src_a + Dst_a * (1f - Src_a);
                    Out_r = Src_r + Dst_r - 2f * Src_r * Dst_r;
                    Out_g = Src_g + Dst_g - 2f * Src_g * Dst_g;
                    Out_b = Src_b + Dst_b - 2f * Src_b * Dst_b;
                    break;
                case CompositeMode.Multiply:
                    Out_a = Src_a + Dst_a * (1f - Src_a);
                    Out_r = Src_r * Dst_r + Src_r * (1f - Dst_a) + Dst_r * (1f - Src_a);
                    Out_g = Src_g * Dst_g + Src_g * (1f - Dst_a) + Dst_g * (1f - Src_a);
                    Out_b = Src_b * Dst_b + Src_b * (1f - Dst_a) + Dst_b * (1f - Src_a);
                    break;
                case CompositeMode.HslHue:
                case CompositeMode.HslSaturation:
                case CompositeMode.HslColor:
                case CompositeMode.HslLuminosity:
                    // HSL non-separable blend modes — composite as SrcOver (best-effort).
                    Out_a = Src_a + Dst_a * (1f - Src_a);
                    Out_r = Src_r + Dst_r * (1f - Src_a);
                    Out_g = Src_g + Dst_g * (1f - Src_a);
                    Out_b = Src_b + Dst_b * (1f - Src_a);
                    break;
            }

            // Convert premultiplied float back to non-premultiplied byte and
            // source-over blend the composite result onto the main surface.
            var outA8 = (byte)(int)Math.Clamp(MathF.Round(Out_a * 255f), 0f, 255f);
            if (outA8 == 0) continue;
            var invA = Out_a > 0f ? 1f / Out_a : 0f;
            var outR8 = (byte)(int)Math.Clamp(MathF.Round(Out_r * invA * 255f), 0f, 255f);
            var outG8 = (byte)(int)Math.Clamp(MathF.Round(Out_g * invA * 255f), 0f, 255f);
            var outB8 = (byte)(int)Math.Clamp(MathF.Round(Out_b * invA * 255f), 0f, 255f);

            // Composite result over the destination surface using source-over.
            var dstPrev_a = dst[pi + 3];
            var srcA_f = outA8 / 255f;
            var dstA_f = dstPrev_a / 255f;
            var resA_f = srcA_f + dstA_f * (1f - srcA_f);
            var resA8 = (byte)(int)Math.Clamp(MathF.Round(resA_f * 255f), 0f, 255f);

            if (resA8 == 0) { dst[pi] = dst[pi + 1] = dst[pi + 2] = dst[pi + 3] = 0; continue; }

            var inv2 = 255 - outA8;
            dst[pi]     = (byte)((outR8 * outA8 + dst[pi]     * inv2 + 127) / 255);
            dst[pi + 1] = (byte)((outG8 * outA8 + dst[pi + 1] * inv2 + 127) / 255);
            dst[pi + 2] = (byte)((outB8 * outA8 + dst[pi + 2] * inv2 + 127) / 255);
            dst[pi + 3] = (byte)Math.Min(255, outA8 + (dst[pi + 3] * inv2 + 127) / 255);
        }
    }

    // ---- Porter-Duff channel helpers ----------------------------------------

    /// <summary>Hard-light blend channel (premultiplied inputs).</summary>
    private static float HardLightChannel(float Sc, float Sa, float Dc, float Da)
    {
        // Sc, Dc are premultiplied by their respective alphas.
        var sc = Sa > 0 ? Sc / Sa : 0f;   // un-premultiply source channel
        var dc = Da > 0 ? Dc / Da : 0f;   // un-premultiply dest channel
        float blended;
        if (sc <= 0.5f)
            blended = 2f * sc * dc;
        else
            blended = 1f - 2f * (1f - sc) * (1f - dc);
        // Re-premultiply for the Porter-Duff composite formula.
        return blended * Sa * Da + Sc * (1f - Da) + Dc * (1f - Sa);
    }

    /// <summary>Soft-light blend channel (premultiplied inputs).</summary>
    private static float SoftLightChannel(float Sc, float Sa, float Dc, float Da)
    {
        var sc = Sa > 0 ? Sc / Sa : 0f;
        var dc = Da > 0 ? Dc / Da : 0f;
        float blended;
        if (sc <= 0.5f)
            blended = dc - (1f - 2f * sc) * dc * (1f - dc);
        else
        {
            float d;
            if (dc <= 0.25f)
                d = ((16f * dc - 12f) * dc + 4f) * dc;
            else
                d = MathF.Sqrt(dc);
            blended = dc + (2f * sc - 1f) * (d - dc);
        }
        return blended * Sa * Da + Sc * (1f - Da) + Dc * (1f - Sa);
    }

    /// <summary>Color-dodge blend channel (premultiplied inputs).</summary>
    private static float DodgeChannel(float Sc, float Sa, float Dc, float Da)
    {
        if (Dc == 0) return Sc * (1f - Da);
        if (Sc == Sa) return Sa * Da + Sc * (1f - Da) + Dc * (1f - Sa);
        var t = Math.Min(Da, Dc * Sa / (Sa - Sc));
        return t * Sa + Sc * (1f - Da) + Dc * (1f - Sa);
    }

    /// <summary>Color-burn blend channel (premultiplied inputs).</summary>
    private static float BurnChannel(float Sc, float Sa, float Dc, float Da)
    {
        if (Dc == Da) return Sa * Da + Sc * (1f - Da) + Dc * (1f - Sa);
        if (Sc == 0) return Dc * (1f - Sa);
        var t = Math.Max(0f, Da - (Da - Dc) * Sa / Sc);
        return t * Sa + Sc * (1f - Da) + Dc * (1f - Sa);
    }

    /// <summary>
    /// A glyph outline mask rendered in surface space: the alpha coverage buffer
    /// plus its bounding box within the surface.
    /// </summary>
    private readonly struct OutlineMask
    {
        /// <summary>Alpha coverage for the bbox region (maskWidth × maskHeight).</summary>
        public readonly byte[] Alpha;
        /// <summary>Left edge of the mask in surface coordinates.</summary>
        public readonly int X0;
        /// <summary>Top edge of the mask in surface coordinates.</summary>
        public readonly int Y0;
        /// <summary>Width of the mask region.</summary>
        public readonly int MaskWidth;
        /// <summary>Height of the mask region.</summary>
        public readonly int MaskHeight;

        public OutlineMask(byte[] alpha, int x0, int y0, int maskWidth, int maskHeight)
        {
            Alpha = alpha;
            X0 = x0;
            Y0 = y0;
            MaskWidth = maskWidth;
            MaskHeight = maskHeight;
        }
    }

    private static OutlineMask? RenderOutlineMask(OpenTypeFont font, uint glyphId,
        int surfaceWidth, int surfaceHeight, in Matrix3x2 xform)
    {
        if (glyphId >= font.NumGlyphs) return null;
        var capturedXform = xform; // can't capture by ref in lambda
        var bmp = SmoothRasterizer.Rasterize(
            sink => font.DrawGlyph(glyphId, new TransformingSink(sink, capturedXform)),
            pixelsPerEm: 1, unitsPerEm: 1);
        if (bmp.IsEmpty) return null;

        // The rasterizer crops to the glyph's bounding box. Compute the bbox
        // position in surface coordinates and clamp to the surface bounds.
        var rawX0 = bmp.Left;
        var rawY0 = -bmp.Top;

        // Clamp the mask bbox to the surface. Pixels outside the surface are
        // discarded — we only keep the intersection.
        var clampedX0 = Math.Max(rawX0, 0);
        var clampedY0 = Math.Max(rawY0, 0);
        var clampedX1 = Math.Min(rawX0 + bmp.Width, surfaceWidth);
        var clampedY1 = Math.Min(rawY0 + bmp.Height, surfaceHeight);

        var maskW = clampedX1 - clampedX0;
        var maskH = clampedY1 - clampedY0;
        if (maskW <= 0 || maskH <= 0) return null;

        // Copy the clamped region from the rasterizer's output into a compact buffer.
        var alpha = new byte[maskW * maskH];
        var srcOffX = clampedX0 - rawX0;
        var srcOffY = clampedY0 - rawY0;
        for (var ry = 0; ry < maskH; ry++)
        {
            var srcRow = (srcOffY + ry) * bmp.Width + srcOffX;
            var dstRow = ry * maskW;
            bmp.Alpha.AsSpan(srcRow, maskW).CopyTo(alpha.AsSpan(dstRow, maskW));
        }

        return new OutlineMask(alpha, clampedX0, clampedY0, maskW, maskH);
    }

    private static Rgba32 SampleFill(PaintRef fill, Vector2 designPos, Rgba32[] palette,
        in Matrix3x2 invFillXform, bool hasFillXform)
    {
        switch (fill.Format)
        {
            case PaintFormat.Solid:
            case PaintFormat.VarSolid:
            {
                // VarSolid layout is identical to Solid up to the varIndexBase suffix;
                // AsSolid() reads only the base fields, which is correct here.
                var d = fill.AsSolid();
                return LookupColor(palette, d.PaletteIndex, d.Alpha);
            }
            case PaintFormat.LinearGradient:
            case PaintFormat.VarLinearGradient:
            {
                var d = fill.AsLinearGradient(default!);
                var p = hasFillXform ? Vector2.Transform(designPos, invFillXform) : designPos;
                var t = ProjectLinearGradient(p,
                    new Vector2(d.X0, d.Y0), new Vector2(d.X1, d.Y1), new Vector2(d.X2, d.Y2));
                return SampleStops(d.Stops, t, palette, d.Extend);
            }
            case PaintFormat.RadialGradient:
            case PaintFormat.VarRadialGradient:
            {
                var d = fill.AsRadialGradient(default!);
                var p = hasFillXform ? Vector2.Transform(designPos, invFillXform) : designPos;
                var t = ProjectRadialGradient(p,
                    new Vector2(d.X0, d.Y0), d.R0, new Vector2(d.X1, d.Y1), d.R1);
                return SampleStops(d.Stops, t, palette, d.Extend);
            }
            case PaintFormat.SweepGradient:
            case PaintFormat.VarSweepGradient:
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
        // Per COLR spec: the gradient direction is perpendicular to line(p0, p2).
        // Rotate (p2 - p0) by 90° to get the gradient axis direction, then
        // project p along that axis with p0 → 0 and p1 → 1.
        var v02 = p2 - p0;
        var gradDir = new Vector2(-v02.Y, v02.X); // 90° rotation of v02

        var denom = Vector2.Dot(p1 - p0, gradDir);
        if (MathF.Abs(denom) <= 1e-6f)
        {
            // p0→p1 is perpendicular to gradient direction (degenerate).
            // Fall back to simple p0→p1 projection.
            var v01 = p1 - p0;
            var lenSq = v01.LengthSquared();
            if (lenSq <= 1e-6f) return 0f;
            return Vector2.Dot(p - p0, v01) / lenSq;
        }
        return Vector2.Dot(p - p0, gradDir) / denom;
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
        var maskResult = RenderOutlineMask(font, glyphId, surface.Width, surface.Height, xform);
        if (maskResult is not { } mask) return;
        for (var my = 0; my < mask.MaskHeight; my++)
        {
            var sy = mask.Y0 + my;
            for (var mx = 0; mx < mask.MaskWidth; mx++)
            {
                var a = mask.Alpha[my * mask.MaskWidth + mx];
                if (a != 0) surface.BlendOver(mask.X0 + mx, sy, color, a);
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

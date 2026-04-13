using System.Globalization;
using System.Numerics;
using System.Text;
using SharpAstro.Fonts.Outlines;
using SharpAstro.Fonts.Tables.Colr;

namespace SharpAstro.Fonts.Color;

/// <summary>
/// Serialize a COLR (v0 + v1) color glyph as a self-contained SVG document.
/// Browsers render the output natively — useful as both a real export
/// format and a high-fidelity visual debugger for the paint-tree walker.
///
/// <para>Coordinate convention: paths and gradient endpoints are emitted in
/// the font's design-unit space (Y-up). The outer
/// <c>&lt;g transform="scale(1,-1)"&gt;</c> wrapper handles the SVG Y-down
/// flip. <see cref="PaintFormat.Transform"/> / Translate / Scale / Rotate /
/// Skew are baked into path + gradient coordinates so a single coordinate
/// system suffices.</para>
///
/// <para>Limitations: <see cref="PaintFormat.SweepGradient"/> falls back to
/// a solid color from the middle stop (SVG has no native conic gradient).
/// <see cref="PaintFormat.Composite"/> renders src-over only (other blend
/// modes ignored). <c>Var*</c> paints render nothing.</para>
/// </summary>
public static class ColrSvgWriter
{
    /// <summary>
    /// Build an SVG for <paramref name="glyphId"/>. Returns null if the font
    /// has no COLR data or the glyph isn't a color glyph.
    /// </summary>
    public static string? ToSvg(OpenTypeFont font, uint glyphId, string title = "")
    {
        if (font.Colr is null) return null;
        var palette = font.Cpal?.GetPalette(0).ToArray() ?? Array.Empty<Rgba32>();

        var ctx = new EmitContext(font, palette);

        if (font.Colr.HasV1 && font.Colr.TryGetV1RootPaint(glyphId, out var root))
        {
            EmitPaint(ctx, root, Matrix3x2.Identity);
        }
        else
        {
            var v0 = font.Colr.GetV0Layers(glyphId);
            if (v0.Length == 0) return null;
            foreach (var layer in v0)
                EmitV0Layer(ctx, layer);
        }

        if (ctx.Body.Length == 0) return null;
        return Wrap(ctx.Body.ToString(), ctx.Defs.ToString(),
            font.Head.XMin, font.Head.YMin, font.Head.XMax, font.Head.YMax, title);
    }

    // ---- Paint-tree walker -------------------------------------------------

    private static void EmitPaint(EmitContext ctx, PaintRef p, Matrix3x2 xform)
    {
        if (p.IsNull) return;
        switch (p.Format)
        {
            case PaintFormat.ColrLayers:
            {
                var d = p.AsColrLayers();
                for (var i = 0; i < d.NumLayers; i++)
                    EmitPaint(ctx, ctx.Font.Colr!.GetLayerPaint((int)(d.FirstLayerIndex + i)), xform);
                break;
            }
            case PaintFormat.ColrGlyph:
            {
                var d = p.AsColrGlyph();
                if (ctx.Font.Colr!.TryGetV1RootPaint(d.GlyphID, out var sub))
                    EmitPaint(ctx, sub, xform);
                break;
            }
            case PaintFormat.Glyph:
            {
                var d = p.AsGlyph();
                EmitGlyphLayer(ctx, d, xform);
                break;
            }
            case PaintFormat.Composite:
            {
                var d = p.AsComposite();
                // Best-effort: backdrop then source (src-over). Other blend
                // modes are ignored; a real implementation would set
                // mix-blend-mode and group with isolation: isolate.
                EmitPaint(ctx, d.Backdrop, xform);
                EmitPaint(ctx, d.Source, xform);
                break;
            }

            // Transforms — accumulate.
            case PaintFormat.Transform:
            {
                var d = p.AsTransform();
                EmitPaint(ctx, d.Paint, d.Transform * xform);
                break;
            }
            case PaintFormat.Translate:
            {
                var d = p.AsTranslate();
                EmitPaint(ctx, d.Paint, Matrix3x2.CreateTranslation(d.Dx, d.Dy) * xform);
                break;
            }
            case PaintFormat.Scale:
            case PaintFormat.ScaleAroundCenter:
            case PaintFormat.ScaleUniform:
            case PaintFormat.ScaleUniformAroundCenter:
            {
                var ac = p.Format is PaintFormat.ScaleAroundCenter
                    or PaintFormat.ScaleUniformAroundCenter;
                var u = p.Format is PaintFormat.ScaleUniform
                    or PaintFormat.ScaleUniformAroundCenter;
                var d = p.AsScale(ac, u);
                var m = ac
                    ? Matrix3x2.CreateScale(d.Sx, d.Sy, new Vector2(d.Cx, d.Cy))
                    : Matrix3x2.CreateScale(d.Sx, d.Sy);
                EmitPaint(ctx, d.Paint, m * xform);
                break;
            }
            case PaintFormat.Rotate:
            case PaintFormat.RotateAroundCenter:
            {
                var d = p.AsRotate(p.Format == PaintFormat.RotateAroundCenter);
                var rad = d.AngleTurns * MathF.PI;
                var m = p.Format == PaintFormat.RotateAroundCenter
                    ? Matrix3x2.CreateRotation(rad, new Vector2(d.Cx, d.Cy))
                    : Matrix3x2.CreateRotation(rad);
                EmitPaint(ctx, d.Paint, m * xform);
                break;
            }
            case PaintFormat.Skew:
            case PaintFormat.SkewAroundCenter:
            {
                var d = p.AsSkew(p.Format == PaintFormat.SkewAroundCenter);
                var xTan = MathF.Tan(d.XAngleTurns * MathF.PI);
                var yTan = MathF.Tan(d.YAngleTurns * MathF.PI);
                var skew = new Matrix3x2(1, yTan, xTan, 1, 0, 0);
                if (p.Format == PaintFormat.SkewAroundCenter)
                {
                    var c = new Vector2(d.Cx, d.Cy);
                    skew = Matrix3x2.CreateTranslation(-c) * skew * Matrix3x2.CreateTranslation(c);
                }
                EmitPaint(ctx, d.Paint, skew * xform);
                break;
            }
            // Var* and unsupported: ignore.
        }
    }

    private static void EmitGlyphLayer(EmitContext ctx, PaintGlyphData glyphPaint, Matrix3x2 xform)
    {
        // Strip transforms from inner fill — accumulate into a separate fillXform
        // so gradient endpoints can be transformed independently of the path.
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

        // The gradient's coordinates live in design space (post-fillXform),
        // and the rendered path lives in xform-applied space. So gradient
        // endpoints in the SVG output must be xform-applied too.
        var gradXform = fillXform * xform;

        // Build the path's d-attribute with xform baked in.
        var pathSink = new SvgPathSink();
        ctx.Font.DrawGlyph(glyphPaint.GlyphID, new XformSink(pathSink, xform));
        var pathData = pathSink.PathData;
        if (string.IsNullOrEmpty(pathData)) return;

        var fillAttr = ResolveFill(ctx, fill, gradXform);
        ctx.Body.Append("    <path fill-rule=\"nonzero\" ").Append(fillAttr)
                .Append(" d=\"").Append(pathData).Append("\"/>\n");
    }

    private static void EmitV0Layer(EmitContext ctx, ColrV0Layer layer)
    {
        var color = LookupColor(ctx.Palette, layer.PaletteIndex);
        var pathSink = new SvgPathSink();
        ctx.Font.DrawGlyph(layer.GlyphId, pathSink);
        if (string.IsNullOrEmpty(pathSink.PathData)) return;
        ctx.Body.Append("    <path fill-rule=\"nonzero\" fill=\"").Append(ToHex(color))
                .Append('"');
        if (color.A != 255)
            ctx.Body.AppendFormat(CultureInfo.InvariantCulture, " fill-opacity=\"{0:0.###}\"", color.A / 255f);
        ctx.Body.Append(" d=\"").Append(pathSink.PathData).Append("\"/>\n");
    }

    // ---- Fill resolution ---------------------------------------------------

    private static string ResolveFill(EmitContext ctx, PaintRef fill, in Matrix3x2 gradXform)
    {
        switch (fill.Format)
        {
            case PaintFormat.Solid:
            {
                var d = fill.AsSolid();
                var color = LookupColor(ctx.Palette, d.PaletteIndex);
                var alpha = (color.A / 255f) * d.Alpha;
                var fa = alpha < 0.999f
                    ? string.Create(CultureInfo.InvariantCulture, $" fill-opacity=\"{alpha:0.###}\"")
                    : "";
                return $"fill=\"{ToHex(color)}\"{fa}";
            }
            case PaintFormat.LinearGradient:
            {
                var d = fill.AsLinearGradient(default!);
                var p0 = Vector2.Transform(new Vector2(d.X0, d.Y0), gradXform);
                var p1 = Vector2.Transform(new Vector2(d.X1, d.Y1), gradXform);
                var id = $"g{ctx.NextId++}";
                EmitLinearDef(ctx, id, p0, p1, d.Extend, d.Stops);
                return $"fill=\"url(#{id})\"";
            }
            case PaintFormat.RadialGradient:
            {
                var d = fill.AsRadialGradient(default!);
                var c0 = Vector2.Transform(new Vector2(d.X0, d.Y0), gradXform);
                var c1 = Vector2.Transform(new Vector2(d.X1, d.Y1), gradXform);
                // Approximate circle scaling for non-uniform xform via length of
                // a unit vector → reasonable for the common scale+translate case.
                var rScale = (Vector2.Transform(new Vector2(1, 0), gradXform - Matrix3x2.CreateTranslation(gradXform.Translation))).Length();
                var r0 = d.R0 * rScale;
                var r1 = d.R1 * rScale;
                var id = $"g{ctx.NextId++}";
                EmitRadialDef(ctx, id, c0, r0, c1, r1, d.Extend, d.Stops);
                return $"fill=\"url(#{id})\"";
            }
            case PaintFormat.SweepGradient:
            {
                // SVG has no native conic gradient. Fall back to the middle stop's color.
                var d = fill.AsSweepGradient(default!);
                if (d.Stops.Length == 0) return "fill=\"none\"";
                var mid = d.Stops[d.Stops.Length / 2];
                var color = LookupColor(ctx.Palette, mid.PaletteIndex)
                    .WithMultipliedAlpha(mid.Alpha);
                return $"fill=\"{ToHex(color)}\"";
            }
            default:
                return "fill=\"#808080\"";
        }
    }

    private static void EmitLinearDef(EmitContext ctx, string id, Vector2 p0, Vector2 p1,
        GradientExtend extend, ColorStop[] stops)
    {
        ctx.Defs.AppendFormat(CultureInfo.InvariantCulture,
            "    <linearGradient id=\"{0}\" gradientUnits=\"userSpaceOnUse\" " +
            "x1=\"{1:0.###}\" y1=\"{2:0.###}\" x2=\"{3:0.###}\" y2=\"{4:0.###}\" spreadMethod=\"{5}\">\n",
            id, p0.X, p0.Y, p1.X, p1.Y, ExtendToSpread(extend));
        EmitStops(ctx, stops);
        ctx.Defs.Append("    </linearGradient>\n");
    }

    private static void EmitRadialDef(EmitContext ctx, string id,
        Vector2 c0, float r0, Vector2 c1, float r1,
        GradientExtend extend, ColorStop[] stops)
    {
        // SVG2 supports fr (focal radius); older renderers ignore it. We map the
        // larger-radius circle to (cx, cy, r) and the smaller to (fx, fy, fr).
        Vector2 outer, focal;
        float outerR, focalR;
        if (r1 >= r0)
        { outer = c1; outerR = r1; focal = c0; focalR = r0; }
        else
        { outer = c0; outerR = r0; focal = c1; focalR = r1; }

        ctx.Defs.AppendFormat(CultureInfo.InvariantCulture,
            "    <radialGradient id=\"{0}\" gradientUnits=\"userSpaceOnUse\" " +
            "cx=\"{1:0.###}\" cy=\"{2:0.###}\" r=\"{3:0.###}\" " +
            "fx=\"{4:0.###}\" fy=\"{5:0.###}\" fr=\"{6:0.###}\" spreadMethod=\"{7}\">\n",
            id, outer.X, outer.Y, outerR, focal.X, focal.Y, focalR, ExtendToSpread(extend));
        EmitStops(ctx, stops);
        ctx.Defs.Append("    </radialGradient>\n");
    }

    private static void EmitStops(EmitContext ctx, ColorStop[] stops)
    {
        foreach (var s in stops)
        {
            var color = LookupColor(ctx.Palette, s.PaletteIndex);
            var alpha = (color.A / 255f) * s.Alpha;
            ctx.Defs.AppendFormat(CultureInfo.InvariantCulture,
                "      <stop offset=\"{0:0.###}\" stop-color=\"{1}\" stop-opacity=\"{2:0.###}\"/>\n",
                s.StopOffset, ToHex(color), alpha);
        }
    }

    // ---- Helpers -----------------------------------------------------------

    private static string ExtendToSpread(GradientExtend e) => e switch
    {
        GradientExtend.Repeat  => "repeat",
        GradientExtend.Reflect => "reflect",
        _                      => "pad",
    };

    private static Rgba32 LookupColor(Rgba32[] palette, ushort index)
    {
        if (index == 0xFFFF) return Rgba32.Black;
        return index < palette.Length ? palette[index] : Rgba32.Black;
    }

    private static string ToHex(Rgba32 c)
        => string.Create(CultureInfo.InvariantCulture, $"#{c.R:X2}{c.G:X2}{c.B:X2}");

    private static string Wrap(string body, string defs,
        int xMin, int yMin, int xMax, int yMax, string title)
    {
        if (xMax <= xMin) { xMin = 0; xMax = 1; }
        if (yMax <= yMin) { yMin = 0; yMax = 1; }
        var width = xMax - xMin;
        var height = yMax - yMin;

        var sb = new StringBuilder(body.Length + defs.Length + 256);
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        sb.Append(CultureInfo.InvariantCulture,
            $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"{xMin} {-yMax} {width} {height}\" " +
            $"width=\"{width}\" height=\"{height}\">\n");
        if (!string.IsNullOrEmpty(title))
            sb.Append("  <title>").Append(System.Net.WebUtility.HtmlEncode(title)).Append("</title>\n");
        if (defs.Length > 0)
            sb.Append("  <defs>\n").Append(defs).Append("  </defs>\n");
        // Y-flip so font's Y-up renders correctly in SVG's Y-down.
        sb.Append("  <g transform=\"scale(1,-1)\">\n");
        sb.Append(body);
        sb.Append("  </g>\n");
        sb.Append("</svg>\n");
        return sb.ToString();
    }

    // ---- Inner types -------------------------------------------------------

    private sealed class EmitContext
    {
        public OpenTypeFont Font { get; }
        public Rgba32[] Palette { get; }
        public StringBuilder Body { get; } = new(2048);
        public StringBuilder Defs { get; } = new(512);
        public int NextId;

        public EmitContext(OpenTypeFont font, Rgba32[] palette)
        { Font = font; Palette = palette; }
    }

    /// <summary>
    /// Affine-transform sink that forwards glyph coordinates AS-IS (no Y-flip).
    /// Used to bake COLR PaintTransform into SVG path coordinates while keeping
    /// font-Y-up convention; the outer SVG &lt;g scale(1,-1)&gt; flips at render
    /// time.
    /// </summary>
    private sealed class XformSink : IGlyphSink
    {
        private readonly IGlyphSink _inner;
        private readonly Matrix3x2 _m;

        public XformSink(IGlyphSink inner, Matrix3x2 m) { _inner = inner; _m = m; }
        private Vector2 Tx(float x, float y) => Vector2.Transform(new Vector2(x, y), _m);

        public void MoveTo(float x, float y) { var p = Tx(x, y); _inner.MoveTo(p.X, p.Y); }
        public void LineTo(float x, float y) { var p = Tx(x, y); _inner.LineTo(p.X, p.Y); }
        public void QuadTo(float cx, float cy, float x, float y)
        { var c = Tx(cx, cy); var p = Tx(x, y); _inner.QuadTo(c.X, c.Y, p.X, p.Y); }
        public void CubicTo(float c1x, float c1y, float c2x, float c2y, float x, float y)
        { var c1 = Tx(c1x, c1y); var c2 = Tx(c2x, c2y); var p = Tx(x, y);
          _inner.CubicTo(c1.X, c1.Y, c2.X, c2.Y, p.X, p.Y); }
        public void Close() => _inner.Close();
    }
}

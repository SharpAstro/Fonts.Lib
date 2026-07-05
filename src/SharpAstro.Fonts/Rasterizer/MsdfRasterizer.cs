using SharpAstro.Fonts.Outlines;
using SharpAstro.Fonts.Rasterizer.Msdf;

namespace SharpAstro.Fonts.Rasterizer;

/// <summary>
/// Multi-channel signed distance field rasterizer (the msdfgen algorithm; see
/// <see cref="Msdf"/> for the ported distance math). Unlike <see cref="SdfRasterizer"/>,
/// which flattens curves to line segments and measures a plain Euclidean
/// distance, this keeps each edge as its true line/quadratic/cubic segment so the
/// per-channel selector can preserve sharp corners.
///
/// <para>Output is an <see cref="MtsdfBitmap"/>: RGB hold the multi-channel signed
/// pseudo-distance, A holds the true signed distance. The grid, padding, and
/// Left/Top positioning match <see cref="SdfRasterizer.RasterizeAuto"/>, and the
/// distance encoding is identical (±<paramref name="spread"/> pixels → [0, 1]),
/// so the A channel is a drop-in replacement for the single-channel field.</para>
///
/// <para>Stateless — every call allocates its own scratch. TrueType (<c>glyf</c>)
/// is the verified path; CFF/Type2 cubics go through the same code but their
/// CCW-outer winding is only corrected globally by the polarity pass, so an
/// overlapping-contour CFF glyph is not yet guaranteed correct (matches the
/// single-channel path's coverage).</para>
/// </summary>
public static class MsdfRasterizer
{
    /// <summary>
    /// Rasterize the path produced by <paramref name="drawTo"/> into an MTSDF,
    /// computing the output dimensions from the glyph outline bounds plus
    /// <paramref name="spread"/> padding.
    /// </summary>
    /// <param name="drawTo">Callback that draws the glyph outline to a sink.</param>
    /// <param name="pixelsPerEm">Scale: pixels per em.</param>
    /// <param name="unitsPerEm">Font design units per em.</param>
    /// <param name="spread">Half-range in pixels: a signed distance of ±spread maps to the full [0, 1] range.</param>
    public static MtsdfBitmap RasterizeAuto(
        Action<IGlyphSink> drawTo,
        float pixelsPerEm,
        int unitsPerEm,
        float spread = 4f)
    {
        if (drawTo is null || pixelsPerEm <= 0f || unitsPerEm <= 0 || spread <= 0f)
            return MtsdfBitmap.Empty;

        var scale = pixelsPerEm / unitsPerEm;

        // Build the outline as edge segments in pixel-scaled, y-up space. The scale is a uniform positive factor,
        // so it preserves winding sign — the msdfgen combiner and polarity vote expect the TrueType y-up frame.
        var builder = new ShapeBuilder(scale);
        drawTo(builder);
        var shape = builder.Finish();
        if (shape.IsEmpty)
            return MtsdfBitmap.Empty;

        EdgeColoring.ColorSimple(shape);

        var bounds = shape.ComputeBounds();
        if (!bounds.IsValid)
            return MtsdfBitmap.Empty;

        // Same grid derivation as SdfRasterizer, expressed in the flipped (top-down) pixel frame the bitmap uses:
        //   flippedY = -y, so the glyph top (max y) becomes the min flipped-Y row.
        var pxMin = (int)MathF.Floor((float)bounds.Left);
        var pyMin = (int)MathF.Floor((float)-bounds.Top);
        var pxMax = (int)MathF.Ceiling((float)bounds.Right);
        var pyMax = (int)MathF.Ceiling((float)-bounds.Bottom);

        var pad = (int)MathF.Ceiling(spread);
        var width = pxMax - pxMin + 2 * pad;
        var height = pyMax - pyMin + 2 * pad;
        if (width <= 0 || height <= 0)
            return MtsdfBitmap.Empty;

        // Texel (px, py) centre → pixel-scaled y-up point. Distances therefore come out in pixels; a range of
        // 2*spread makes the +0.5-biased field span exactly [0, 1] over ±spread pixels (matching SdfRasterizer).
        var projection = new CellProjection(BoxLeft: pxMin - pad, BoxTop: -(pyMin - pad), UnitsPerTexel: 1.0);

        var pixels = new float[width * height * 4];
        MsdfGenerator.Generate(shape, width, height, rangeUnits: 2.0 * spread, projection, pixels);

        var rgba = new byte[width * height * 4];
        for (var i = 0; i < rgba.Length; i++)
        {
            var v = pixels[i];
            if (v < 0f) v = 0f;
            else if (v > 1f) v = 1f;
            rgba[i] = (byte)(v * 255f + 0.5f);
        }

        return new MtsdfBitmap(rgba, width, height, pxMin - pad, -(pyMin - pad), spread);
    }

    /// <summary>
    /// Builds an msdfgen <see cref="Shape"/> from <see cref="IGlyphSink"/> path
    /// commands, applying the pixel scale (y-up preserved). Each contour is a
    /// closed loop of edge segments; a missing closing edge is synthesized so the
    /// combiner's wrap-around indexing stays valid.
    /// </summary>
    private sealed class ShapeBuilder(float scale) : IGlyphSink
    {
        private readonly Shape _shape = new();
        private readonly float _scale = scale;
        private Contour? _contour;
        private Vector2D _cur;
        private Vector2D _start;
        private bool _hasStart;

        private Vector2D P(float x, float y) => new(x * _scale, y * _scale);

        public void MoveTo(float x, float y)
        {
            CloseContour();
            _contour = new Contour();
            _shape.Contours.Add(_contour);
            _cur = _start = P(x, y);
            _hasStart = true;
        }

        public void LineTo(float x, float y)
        {
            if (_contour is null) return;
            var next = P(x, y);
            _contour.Add(new LinearSegment(_cur, next));
            _cur = next;
        }

        public void QuadTo(float cx, float cy, float x, float y)
        {
            if (_contour is null) return;
            var ctrl = P(cx, cy);
            var next = P(x, y);
            _contour.Add(new QuadraticSegment(_cur, ctrl, next));
            _cur = next;
        }

        public void CubicTo(float c1x, float c1y, float c2x, float c2y, float x, float y)
        {
            if (_contour is null) return;
            var c1 = P(c1x, c1y);
            var c2 = P(c2x, c2y);
            var next = P(x, y);
            _contour.Add(new CubicSegment(_cur, c1, c2, next));
            _cur = next;
        }

        public void Close() => CloseContour();

        public Shape Finish()
        {
            CloseContour();
            // Drop any empty contours so the combiner never sees a zero-edge loop.
            _shape.Contours.RemoveAll(c => c.Edges.Count == 0);
            return _shape;
        }

        private void CloseContour()
        {
            if (_contour is null || !_hasStart)
                return;
            if (_contour.Edges.Count > 0 && (_cur.X != _start.X || _cur.Y != _start.Y))
                _contour.Add(new LinearSegment(_cur, _start));
            _cur = _start;
        }
    }
}

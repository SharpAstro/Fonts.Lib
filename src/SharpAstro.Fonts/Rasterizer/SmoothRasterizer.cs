using SharpAstro.Fonts.Outlines;

namespace SharpAstro.Fonts.Rasterizer;

/// <summary>
/// Anti-aliased scanline rasterizer. Converts an <see cref="Outline"/> in
/// design units into an 8-bit grayscale alpha bitmap.
///
/// <para>Algorithm: recursive midpoint subdivision of quadratic Béziers into
/// line segments at sub-pixel flatness; then per-output-row vertical
/// supersampling (default 4×) combined with exact sub-pixel-X coverage at
/// edge crossings. Fill rule: non-zero winding.</para>
///
/// <para>Stateless — every call allocates its own scratch. Safe to invoke
/// concurrently from any thread.</para>
/// </summary>
public static class SmoothRasterizer
{
    /// <summary>Default vertical supersampling factor — 4 gives 17 alpha levels.</summary>
    public const int DefaultSubSamples = 4;

    /// <summary>
    /// Rasterize <paramref name="outline"/> at <paramref name="pixelsPerEm"/>.
    /// </summary>
    public static GlyphBitmap Rasterize(Outline outline, float pixelsPerEm,
        int unitsPerEm, int subSamples = DefaultSubSamples)
    {
        if (outline.IsEmpty || pixelsPerEm <= 0 || unitsPerEm <= 0)
            return GlyphBitmap.Empty;
        if (subSamples < 1) subSamples = 1;

        var scale = pixelsPerEm / (float)unitsPerEm;

        // Collect all edges in world pixel space with the baseline at y=0:
        //   pixel_x = font_x * scale
        //   pixel_y = -font_y * scale   (font Y-up → bitmap Y-down)
        var collector = new EdgeCollector(scale, offsetX: 0f, offsetY: 0f);
        BezierFlattener.Walk(outline, collector);

        if (collector.EdgeCount == 0) return GlyphBitmap.Empty;

        // Bitmap covers the actual edge bbox (which may extend slightly past
        // the font-recorded bounds for high-curvature beziers).
        var pxMinW = (int)MathF.Floor(collector.MinX);
        var pyMinW = (int)MathF.Floor(collector.MinY);
        var pxMaxW = (int)MathF.Ceiling(collector.MaxX);
        var pyMaxW = (int)MathF.Ceiling(collector.MaxY);
        var width = pxMaxW - pxMinW;
        var height = pyMaxW - pyMinW;
        if (width <= 0 || height <= 0) return GlyphBitmap.Empty;

        // Shift edges so the bitmap top-left corresponds to (pxMinW, pyMinW).
        collector.Offset(-pxMinW, -pyMinW);

        var alpha = new byte[width * height];
        Render(collector, width, height, subSamples, alpha);

        // Left = pixel offset from pen X to bitmap left edge.
        // Top  = pixel offset from baseline (Y=0 in world) to bitmap top edge,
        //        positive when bitmap is above the baseline (FreeType convention).
        return new GlyphBitmap(alpha, width, height, left: pxMinW, top: -pyMinW);
    }

    // ---- Rendering ----------------------------------------------------------

    private static void Render(EdgeCollector edges, int width, int height,
        int subSamples, byte[] alpha)
    {
        var coverage = new float[width];
        var crossings = new (float X, int Wind)[edges.EdgeCount];
        var invSub = 1f / subSamples;

        var xs = edges.X0;
        var ys = edges.Y0;
        var xs1 = edges.X1;
        var ys1 = edges.Y1;
        var n = edges.EdgeCount;

        for (var py = 0; py < height; py++)
        {
            Array.Clear(coverage);

            for (var s = 0; s < subSamples; s++)
            {
                var sy = py + (s + 0.5f) * invSub;

                // Build active-edge crossings.
                var k = 0;
                for (var i = 0; i < n; i++)
                {
                    var y0 = ys[i];
                    var y1 = ys1[i];
                    int wind;
                    float ya, yb, xa, xb;
                    if (y0 < y1) { ya = y0; yb = y1; xa = xs[i]; xb = xs1[i]; wind = +1; }
                    else if (y0 > y1) { ya = y1; yb = y0; xa = xs1[i]; xb = xs[i]; wind = -1; }
                    else continue; // horizontal edge contributes no crossing

                    if (sy < ya || sy >= yb) continue;
                    var t = (sy - ya) / (yb - ya);
                    var x = xa + t * (xb - xa);
                    crossings[k++] = (x, wind);
                }
                if (k == 0) continue;

                // Sort active crossings by X (insertion sort — k is typically tiny).
                for (var i = 1; i < k; i++)
                {
                    var key = crossings[i];
                    var j = i - 1;
                    while (j >= 0 && crossings[j].X > key.X)
                    {
                        crossings[j + 1] = crossings[j];
                        j--;
                    }
                    crossings[j + 1] = key;
                }

                // Non-zero winding scan.
                var winding = 0;
                float spanStart = 0;
                var inSpan = false;
                for (var i = 0; i < k; i++)
                {
                    var prevWind = winding;
                    winding += crossings[i].Wind;
                    var x = crossings[i].X;

                    if (prevWind == 0 && winding != 0)
                    {
                        spanStart = x;
                        inSpan = true;
                    }
                    else if (prevWind != 0 && winding == 0 && inSpan)
                    {
                        AccumulateSpan(coverage, spanStart, x, invSub, width);
                        inSpan = false;
                    }
                }
                // Defensive: ignore unmatched span (shouldn't happen for sane outlines).
            }

            var rowOffset = py * width;
            for (var px = 0; px < width; px++)
            {
                var c = coverage[px];
                if (c < 0f) c = 0f; else if (c > 1f) c = 1f;
                alpha[rowOffset + px] = (byte)(c * 255f + 0.5f);
            }
        }
    }

    /// <summary>
    /// Add fractional coverage for a horizontal span <c>[xL, xR)</c> weighted
    /// by <paramref name="weight"/> (= 1 / subSamples). Handles partial pixels
    /// at both ends.
    /// </summary>
    private static void AccumulateSpan(float[] coverage, float xL, float xR,
        float weight, int width)
    {
        if (xR <= 0 || xL >= width || xL >= xR) return;
        if (xL < 0) xL = 0;
        if (xR > width) xR = width;

        var ixL = (int)MathF.Floor(xL);
        var ixR = (int)MathF.Floor(xR);

        if (ixL == ixR)
        {
            coverage[ixL] += (xR - xL) * weight;
            return;
        }
        coverage[ixL] += (1f - (xL - ixL)) * weight;
        for (var x = ixL + 1; x < ixR; x++)
            coverage[x] += weight;
        if (ixR < width)
            coverage[ixR] += (xR - ixR) * weight;
    }

    // ---- Edge collector (IGlyphSink) ---------------------------------------

    private sealed class EdgeCollector : IGlyphSink
    {
        private const int InitialCapacity = 64;
        // Edges stored as parallel arrays for tight memory + cache locality.
        private float[] _x0 = new float[InitialCapacity];
        private float[] _y0 = new float[InitialCapacity];
        private float[] _x1 = new float[InitialCapacity];
        private float[] _y1 = new float[InitialCapacity];
        public int EdgeCount { get; private set; }

        public ReadOnlySpan<float> X0 => _x0.AsSpan(0, EdgeCount);
        public ReadOnlySpan<float> Y0 => _y0.AsSpan(0, EdgeCount);
        public ReadOnlySpan<float> X1 => _x1.AsSpan(0, EdgeCount);
        public ReadOnlySpan<float> Y1 => _y1.AsSpan(0, EdgeCount);

        public float MinX { get; private set; } = float.PositiveInfinity;
        public float MinY { get; private set; } = float.PositiveInfinity;
        public float MaxX { get; private set; } = float.NegativeInfinity;
        public float MaxY { get; private set; } = float.NegativeInfinity;

        private readonly float _scale;
        private readonly float _offsetX;
        // _offsetY is the bitmap-y of font's y=0; with the y-flip the
        // transformed pixel-y is (offsetY - fontY * scale).
        private readonly float _offsetY;
        private float _curX, _curY, _startX, _startY;
        private bool _hasStart;

        public EdgeCollector(float scale, float offsetX, float offsetY)
        {
            _scale = scale;
            _offsetX = offsetX;
            _offsetY = offsetY;
        }

        public void MoveTo(float x, float y)
        {
            _curX = x * _scale + _offsetX;
            _curY = _offsetY - y * _scale;
            _startX = _curX;
            _startY = _curY;
            _hasStart = true;
        }

        public void LineTo(float x, float y)
        {
            var nx = x * _scale + _offsetX;
            var ny = _offsetY - y * _scale;
            AddEdge(_curX, _curY, nx, ny);
            _curX = nx;
            _curY = ny;
        }

        public void QuadTo(float cx, float cy, float x, float y)
        {
            var ncx = cx * _scale + _offsetX;
            var ncy = _offsetY - cy * _scale;
            var nx = x * _scale + _offsetX;
            var ny = _offsetY - y * _scale;
            Subdivide(_curX, _curY, ncx, ncy, nx, ny, depth: 0);
            _curX = nx;
            _curY = ny;
        }

        public void Close()
        {
            if (!_hasStart) return;
            if (_curX != _startX || _curY != _startY)
                AddEdge(_curX, _curY, _startX, _startY);
            _curX = _startX;
            _curY = _startY;
        }

        private void Subdivide(float x0, float y0, float cx, float cy,
            float x1, float y1, int depth)
        {
            // Flatness: control point's perpendicular squared distance to chord.
            var dx = x1 - x0;
            var dy = y1 - y0;
            var cross = (cx - x0) * dy - (cy - y0) * dx;
            var lenSq = dx * dx + dy * dy;
            // Threshold ~ 0.25 pixel² perpendicular distance.
            if (depth >= 16 || cross * cross <= 0.0625f * lenSq + 0.01f)
            {
                AddEdge(x0, y0, x1, y1);
                return;
            }
            var mx0 = (x0 + cx) * 0.5f;
            var my0 = (y0 + cy) * 0.5f;
            var mx1 = (cx + x1) * 0.5f;
            var my1 = (cy + y1) * 0.5f;
            var mx = (mx0 + mx1) * 0.5f;
            var my = (my0 + my1) * 0.5f;
            Subdivide(x0, y0, mx0, my0, mx, my, depth + 1);
            Subdivide(mx, my, mx1, my1, x1, y1, depth + 1);
        }

        private void AddEdge(float x0, float y0, float x1, float y1)
        {
            if (EdgeCount == _x0.Length)
            {
                var newCap = _x0.Length * 2;
                Array.Resize(ref _x0, newCap);
                Array.Resize(ref _y0, newCap);
                Array.Resize(ref _x1, newCap);
                Array.Resize(ref _y1, newCap);
            }
            _x0[EdgeCount] = x0;
            _y0[EdgeCount] = y0;
            _x1[EdgeCount] = x1;
            _y1[EdgeCount] = y1;
            EdgeCount++;

            if (x0 < MinX) MinX = x0;
            if (x1 < MinX) MinX = x1;
            if (x0 > MaxX) MaxX = x0;
            if (x1 > MaxX) MaxX = x1;
            if (y0 < MinY) MinY = y0;
            if (y1 < MinY) MinY = y1;
            if (y0 > MaxY) MaxY = y0;
            if (y1 > MaxY) MaxY = y1;
        }

        public void Offset(float dx, float dy)
        {
            for (var i = 0; i < EdgeCount; i++)
            {
                _x0[i] += dx; _x1[i] += dx;
                _y0[i] += dy; _y1[i] += dy;
            }
            MinX += dx; MaxX += dx;
            MinY += dy; MaxY += dy;
        }
    }
}

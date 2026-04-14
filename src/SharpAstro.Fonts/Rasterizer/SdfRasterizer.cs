using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using SharpAstro.Fonts.Outlines;

namespace SharpAstro.Fonts.Rasterizer;

/// <summary>
/// Signed Distance Field rasterizer. Produces a float[] in [0, 1] where
/// 0.5 is exactly on the glyph edge, values above 0.5 are inside the glyph
/// and values below 0.5 are outside.
///
/// <para>Algorithm:
/// <list type="number">
///   <item>Flatten all curves to line segments (same subdivision as
///   <see cref="SmoothRasterizer"/>).</item>
///   <item>For every pixel centre (px+0.5, py+0.5) in the output grid,
///   compute the minimum Euclidean distance to any edge segment.</item>
///   <item>Determine inside/outside by summing the winding contributions of
///   all edges (non-zero fill rule, consistent with the rasterizer).</item>
///   <item>Normalise: <c>sign * dist / spread</c>, clamped to [−1, 1], then
///   mapped to [0, 1] via <c>(v + 1) * 0.5</c>.</item>
/// </list>
/// </para>
///
/// <para>Stateless — every call allocates its own scratch. Thread-safe.</para>
///
/// <para>The inner edge loop is SIMD-vectorized (Vector128&lt;float&gt;,
/// 4 edges per iteration) for both distance and winding computation.</para>
/// </summary>
public static class SdfRasterizer
{
    /// <summary>
    /// Rasterize the path produced by <paramref name="drawTo"/> into a
    /// <paramref name="width"/>×<paramref name="height"/> signed distance field.
    /// </summary>
    /// <param name="drawTo">Callback that draws the glyph outline to a sink.</param>
    /// <param name="width">Output buffer width in pixels.</param>
    /// <param name="height">Output buffer height in pixels.</param>
    /// <param name="pixelsPerEm">Scale: pixels per em (design units per em).</param>
    /// <param name="unitsPerEm">Font design units per em.</param>
    /// <param name="spread">
    /// Maximum distance (in pixels) that is encoded in the SDF. Pixels further
    /// than <paramref name="spread"/> from the outline are clamped to 0 or 1.
    /// </param>
    /// <returns>
    /// Row-major float[] of size <c>width * height</c>. Values are in [0, 1]:
    /// 0.5 = on edge, &gt;0.5 = inside, &lt;0.5 = outside.
    /// Returns an empty array when the outline is empty.
    /// </returns>
    public static float[] Rasterize(
        Action<IGlyphSink> drawTo,
        int width,
        int height,
        float pixelsPerEm,
        int unitsPerEm,
        float spread = 4f)
    {
        if (drawTo is null || width <= 0 || height <= 0
            || pixelsPerEm <= 0f || unitsPerEm <= 0 || spread <= 0f)
            return [];

        var scale = pixelsPerEm / (float)unitsPerEm;

        // Collect edges in the same coordinate system as SmoothRasterizer:
        //   pixel_x =  font_x * scale
        //   pixel_y = -font_y * scale  (Y-flip so bitmap runs top-to-bottom)
        var collector = new EdgeCollector(scale, offsetX: 0f, offsetY: 0f);
        drawTo(collector);

        if (collector.EdgeCount == 0) return [];

        var n   = collector.EdgeCount;

        // Shift so that the glyph sits inside the requested buffer the same
        // way SmoothRasterizer does (top-left of bbox → pixel 0,0).
        var pxMin = (int)MathF.Floor(collector.MinX);
        var pyMin = (int)MathF.Floor(collector.MinY);
        collector.Offset(-pxMin, -pyMin);

        var result = new float[width * height];

        ref var xs0  = ref MemoryMarshal.GetReference(collector.X0);
        ref var ys0  = ref MemoryMarshal.GetReference(collector.Y0);
        ref var xs10 = ref MemoryMarshal.GetReference(collector.X1);
        ref var ys10 = ref MemoryMarshal.GetReference(collector.Y1);

        for (var py = 0; py < height; py++)
        {
            for (var px = 0; px < width; px++)
            {
                // Pixel centre in the shifted coordinate system.
                var pcx = px + 0.5f;
                var pcy = py + 0.5f;

                ComputePixel(ref xs0, ref ys0, ref xs10, ref ys10, n, pcx, pcy,
                    out var minDistSq, out var winding);

                // Convert to signed distance (negative = inside).
                var dist = MathF.Sqrt(minDistSq);
                var sign = (winding != 0) ? -1f : 1f;   // inside → negative

                // Normalise to [−1, 1] then map to [0, 1].
                var normalised = sign * dist / spread;
                if (normalised < -1f) normalised = -1f;
                else if (normalised > 1f) normalised = 1f;

                // inside → normalised negative → mapped value > 0.5
                result[py * width + px] = (1f - normalised) * 0.5f;
            }
        }

        return result;
    }

    /// <summary>
    /// Rasterize the path produced by <paramref name="drawTo"/> into a signed
    /// distance field, automatically computing the output dimensions from the
    /// glyph outline bounds plus <paramref name="spread"/> padding.
    /// </summary>
    /// <returns>An <see cref="SdfBitmap"/> with Left/Top for glyph positioning.</returns>
    public static SdfBitmap RasterizeAuto(
        Action<IGlyphSink> drawTo,
        float pixelsPerEm,
        int unitsPerEm,
        float spread = 4f)
    {
        if (drawTo is null || pixelsPerEm <= 0f || unitsPerEm <= 0 || spread <= 0f)
            return SdfBitmap.Empty;

        var scale = pixelsPerEm / (float)unitsPerEm;
        var collector = new EdgeCollector(scale, 0f, 0f);
        drawTo(collector);

        if (collector.EdgeCount == 0) return SdfBitmap.Empty;

        var pxMin = (int)MathF.Floor(collector.MinX);
        var pyMin = (int)MathF.Floor(collector.MinY);
        var pxMax = (int)MathF.Ceiling(collector.MaxX);
        var pyMax = (int)MathF.Ceiling(collector.MaxY);

        var pad = (int)MathF.Ceiling(spread);
        var width = pxMax - pxMin + 2 * pad;
        var height = pyMax - pyMin + 2 * pad;

        if (width <= 0 || height <= 0) return SdfBitmap.Empty;

        // Shift edges so glyph bbox starts at (pad, pad) in output buffer
        collector.Offset(-pxMin + pad, -pyMin + pad);

        var n   = collector.EdgeCount;

        ref var xs0  = ref MemoryMarshal.GetReference(collector.X0);
        ref var ys0  = ref MemoryMarshal.GetReference(collector.Y0);
        ref var xs10 = ref MemoryMarshal.GetReference(collector.X1);
        ref var ys10 = ref MemoryMarshal.GetReference(collector.Y1);

        // Write byte[] directly — no intermediate float[] buffer.
        var alpha = new byte[width * height];
        for (var py = 0; py < height; py++)
        {
            for (var px = 0; px < width; px++)
            {
                var pcx = px + 0.5f;
                var pcy = py + 0.5f;

                ComputePixel(ref xs0, ref ys0, ref xs10, ref ys10, n, pcx, pcy,
                    out var minDistSq, out var winding);

                var dist = MathF.Sqrt(minDistSq);
                var sign = (winding != 0) ? -1f : 1f;
                var normalised = sign * dist / spread;
                if (normalised < -1f) normalised = -1f;
                else if (normalised > 1f) normalised = 1f;

                alpha[py * width + px] = (byte)((1f - normalised) * 0.5f * 255f + 0.5f);
            }
        }

        // Left = glyph bbox left in pixel coords minus spread padding
        // Top = pixels above baseline to top of bitmap (Y-flipped)
        return new SdfBitmap(alpha, width, height, pxMin - pad, -(pyMin - pad), spread);
    }

    // ── SIMD pixel computation ──────────────────────────────────────────────

    /// <summary>
    /// Compute minimum squared distance and winding number for a single pixel
    /// against all edges. Uses Vector128 (4 edges per iteration) when the edge
    /// count is ≥ 4, with a scalar tail for the remainder.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ComputePixel(
        ref float xs, ref float ys, ref float xs1, ref float ys1,
        int n, float pcx, float pcy,
        out float minDistSq, out int winding)
    {
        minDistSq = float.MaxValue;
        winding = 0;

        if (Vector128.IsHardwareAccelerated && n >= 4)
        {
            var pcx4 = Vector128.Create(pcx);
            var pcy4 = Vector128.Create(pcy);
            var minDSq4 = Vector128.Create(float.MaxValue);
            var winding4 = Vector128<int>.Zero;
            var zero4 = Vector128<float>.Zero;
            var one4 = Vector128.Create(1f);
            var eps4 = Vector128.Create(1e-12f);

            var i = 0;
            for (; i + 3 < n; i += 4)
            {
                var ax4 = Vector128.LoadUnsafe(ref Unsafe.Add(ref xs, i));
                var ay4 = Vector128.LoadUnsafe(ref Unsafe.Add(ref ys, i));
                var bx4 = Vector128.LoadUnsafe(ref Unsafe.Add(ref xs1, i));
                var by4 = Vector128.LoadUnsafe(ref Unsafe.Add(ref ys1, i));

                // ── SegmentDistSq (vectorized) ──
                var dx4 = bx4 - ax4;
                var dy4 = by4 - ay4;
                var lenSq4 = dx4 * dx4 + dy4 * dy4;

                // t = clamp(dot(P-A, B-A) / lenSq, 0, 1)
                var pax4 = pcx4 - ax4;
                var pay4 = pcy4 - ay4;
                var dot4 = pax4 * dx4 + pay4 * dy4;
                // Guard degenerate segments: if lenSq < eps, t = 0.
                var invLen4 = Vector128.ConditionalSelect(
                    Vector128.GreaterThan(lenSq4, eps4).AsSingle(),
                    one4 / lenSq4,
                    zero4);
                var t4 = Vector128.Min(Vector128.Max(dot4 * invLen4, zero4), one4);

                var qx4 = ax4 + t4 * dx4 - pcx4;
                var qy4 = ay4 + t4 * dy4 - pcy4;
                var dSq4 = qx4 * qx4 + qy4 * qy4;

                minDSq4 = Vector128.Min(minDSq4, dSq4);

                // ── WindingContribution (vectorized) ──
                // isLeft = (bx-ax)*(py-ay) - (by-ay)*(px-ax)
                var isLeft4 = (bx4 - ax4) * (pcy4 - ay4) - (by4 - ay4) * (pcx4 - ax4);

                // Upward crossing: ay <= py && by > py && isLeft > 0 → +1
                var ayLePy = Vector128.LessThanOrEqual(ay4, pcy4);
                var byGtPy = Vector128.GreaterThan(by4, pcy4);
                var leftPos = Vector128.GreaterThan(isLeft4, zero4);
                var upMask = ayLePy & byGtPy & leftPos;

                // Downward crossing: ay > py && by <= py && isLeft < 0 → -1
                var ayGtPy = Vector128.GreaterThan(ay4, pcy4);
                var byLePy = Vector128.LessThanOrEqual(by4, pcy4);
                var leftNeg = Vector128.LessThan(isLeft4, zero4);
                var downMask = ayGtPy & byLePy & leftNeg;

                // +1 for each upward, -1 for each downward.
                var ones4 = Vector128.Create(1);
                var contrib = Vector128.ConditionalSelect(upMask.AsInt32(), ones4, Vector128<int>.Zero)
                            - Vector128.ConditionalSelect(downMask.AsInt32(), ones4, Vector128<int>.Zero);
                winding4 += contrib;
            }

            // Horizontal reduce: min across 4 lanes.
            minDistSq = Vector128.Min(
                Vector128.Min(
                    Vector128.Shuffle(minDSq4, Vector128.Create(0, 0, 0, 0)),
                    Vector128.Shuffle(minDSq4, Vector128.Create(1, 1, 1, 1))),
                Vector128.Min(
                    Vector128.Shuffle(minDSq4, Vector128.Create(2, 2, 2, 2)),
                    Vector128.Shuffle(minDSq4, Vector128.Create(3, 3, 3, 3)))
            ).ToScalar();

            // Sum winding lanes.
            winding = winding4[0] + winding4[1] + winding4[2] + winding4[3];

            // Scalar tail for remaining edges.
            for (; i < n; i++)
            {
                var ax = Unsafe.Add(ref xs, i);
                var ay = Unsafe.Add(ref ys, i);
                var bx = Unsafe.Add(ref xs1, i);
                var by = Unsafe.Add(ref ys1, i);

                winding += WindingContribution(pcx, pcy, ax, ay, bx, by);
                var dSq = SegmentDistSq(pcx, pcy, ax, ay, bx, by);
                if (dSq < minDistSq) minDistSq = dSq;
            }
        }
        else
        {
            // Scalar fallback.
            for (var i = 0; i < n; i++)
            {
                var ax = Unsafe.Add(ref xs, i);
                var ay = Unsafe.Add(ref ys, i);
                var bx = Unsafe.Add(ref xs1, i);
                var by = Unsafe.Add(ref ys1, i);

                winding += WindingContribution(pcx, pcy, ax, ay, bx, by);
                var dSq = SegmentDistSq(pcx, pcy, ax, ay, bx, by);
                if (dSq < minDistSq) minDistSq = dSq;
            }
        }
    }

    // ── Geometry helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Returns the squared distance from point P to the closest point on the
    /// line segment AB.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float SegmentDistSq(
        float px, float py,
        float ax, float ay,
        float bx, float by)
    {
        var dx = bx - ax;
        var dy = by - ay;
        var lenSq = dx * dx + dy * dy;

        float t;
        if (lenSq < 1e-12f)
        {
            // Degenerate segment — treat as a point.
            t = 0f;
        }
        else
        {
            // Project P onto the line, clamp t to [0, 1].
            t = ((px - ax) * dx + (py - ay) * dy) / lenSq;
            if (t < 0f) t = 0f;
            else if (t > 1f) t = 1f;
        }

        var qx = ax + t * dx - px;
        var qy = ay + t * dy - py;
        return qx * qx + qy * qy;
    }

    /// <summary>
    /// Returns +1 or −1 winding contribution for a horizontal ray cast from
    /// point P in the +X direction against segment AB, or 0 if the segment
    /// does not cross the ray. Matches the non-zero fill-rule convention used
    /// by <see cref="SmoothRasterizer"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int WindingContribution(
        float px, float py,
        float ax, float ay,
        float bx, float by)
    {
        // Check whether the segment straddles the horizontal through py.
        if (ay <= py)
        {
            if (by > py)
            {
                // Upward crossing — check if it is to the right of px.
                if (IsLeft(ax, ay, bx, by, px, py) > 0f)
                    return +1;
            }
        }
        else
        {
            if (by <= py)
            {
                // Downward crossing.
                if (IsLeft(ax, ay, bx, by, px, py) < 0f)
                    return -1;
            }
        }
        return 0;
    }

    /// <summary>
    /// 2-D cross product of vectors AB and AP.
    /// Positive → P is to the left of the directed line A→B.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float IsLeft(
        float ax, float ay,
        float bx, float by,
        float px, float py)
        => (bx - ax) * (py - ay) - (by - ay) * (px - ax);

    // ── Edge collector ────────────────────────────────────────────────────────

    /// <summary>
    /// Receives IGlyphSink calls, flattens Béziers to line segments (same
    /// subdivision thresholds as <see cref="SmoothRasterizer"/>), and stores
    /// the results as parallel flat arrays for cache-friendly per-pixel access.
    /// </summary>
    private sealed class EdgeCollector : IGlyphSink
    {
        private const int InitialCapacity = 64;
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
        private readonly float _offsetY;
        private float _curX, _curY, _startX, _startY;
        private bool _hasStart;

        public EdgeCollector(float scale, float offsetX, float offsetY)
        {
            _scale   = scale;
            _offsetX = offsetX;
            _offsetY = offsetY;
        }

        public void MoveTo(float x, float y)
        {
            _curX   = x * _scale + _offsetX;
            _curY   = _offsetY - y * _scale;
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
            var nx  = x  * _scale + _offsetX;
            var ny  = _offsetY - y * _scale;
            Subdivide(_curX, _curY, ncx, ncy, nx, ny, depth: 0);
            _curX = nx;
            _curY = ny;
        }

        public void CubicTo(float c1x, float c1y, float c2x, float c2y, float x, float y)
        {
            var nc1x = c1x * _scale + _offsetX;
            var nc1y = _offsetY - c1y * _scale;
            var nc2x = c2x * _scale + _offsetX;
            var nc2y = _offsetY - c2y * _scale;
            var nx   = x * _scale + _offsetX;
            var ny   = _offsetY - y * _scale;
            SubdivideCubic(_curX, _curY, nc1x, nc1y, nc2x, nc2y, nx, ny, depth: 0);
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
            var dx    = x1 - x0;
            var dy    = y1 - y0;
            var cross = (cx - x0) * dy - (cy - y0) * dx;
            var lenSq = dx * dx + dy * dy;
            if (depth >= 16 || cross * cross <= 0.0625f * lenSq + 0.01f)
            {
                AddEdge(x0, y0, x1, y1);
                return;
            }
            var mx0 = (x0 + cx) * 0.5f;
            var my0 = (y0 + cy) * 0.5f;
            var mx1 = (cx + x1) * 0.5f;
            var my1 = (cy + y1) * 0.5f;
            var mx  = (mx0 + mx1) * 0.5f;
            var my  = (my0 + my1) * 0.5f;
            Subdivide(x0, y0, mx0, my0, mx, my, depth + 1);
            Subdivide(mx, my, mx1, my1, x1, y1, depth + 1);
        }

        private void SubdivideCubic(float x0, float y0,
            float c1x, float c1y, float c2x, float c2y,
            float x1, float y1, int depth)
        {
            var dx = x1 - x0;
            var dy = y1 - y0;
            var cross1 = (c1x - x0) * dy - (c1y - y0) * dx;
            var cross2 = (c2x - x0) * dy - (c2y - y0) * dx;
            var lenSq  = dx * dx + dy * dy;
            var maxCrossSq = MathF.Max(cross1 * cross1, cross2 * cross2);
            if (depth >= 18 || maxCrossSq <= 0.0625f * lenSq + 0.01f)
            {
                AddEdge(x0, y0, x1, y1);
                return;
            }
            var p01x  = (x0 + c1x) * 0.5f;
            var p01y  = (y0 + c1y) * 0.5f;
            var p12x  = (c1x + c2x) * 0.5f;
            var p12y  = (c1y + c2y) * 0.5f;
            var p23x  = (c2x + x1) * 0.5f;
            var p23y  = (c2y + y1) * 0.5f;
            var p012x = (p01x + p12x) * 0.5f;
            var p012y = (p01y + p12y) * 0.5f;
            var p123x = (p12x + p23x) * 0.5f;
            var p123y = (p12y + p23y) * 0.5f;
            var midx  = (p012x + p123x) * 0.5f;
            var midy  = (p012y + p123y) * 0.5f;
            SubdivideCubic(x0, y0, p01x, p01y, p012x, p012y, midx, midy, depth + 1);
            SubdivideCubic(midx, midy, p123x, p123y, p23x, p23y, x1, y1, depth + 1);
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

            if (x0 < MinX) MinX = x0; if (x1 < MinX) MinX = x1;
            if (x0 > MaxX) MaxX = x0; if (x1 > MaxX) MaxX = x1;
            if (y0 < MinY) MinY = y0; if (y1 < MinY) MinY = y1;
            if (y0 > MaxY) MaxY = y0; if (y1 > MaxY) MaxY = y1;
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

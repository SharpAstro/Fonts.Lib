using SharpAstro.Fonts.Outlines;

namespace SharpAstro.Fonts.Rasterizer;

/// <summary>Open-contour endpoint cap style.</summary>
// Public because stroke parameters reach the public surface through ShxFont: SHX glyphs
// are pen paths whose width, cap and join come from the caller's graphics state rather
// than from the font, so they cannot be defaulted away. OutlineStroker itself stays
// internal.
public enum LineCap
{
    /// <summary>Square cut exactly at the endpoint — no extension.</summary>
    Butt,
    /// <summary>Semicircle centred on the endpoint, radius = lineWidth/2.</summary>
    Round,
    /// <summary>Square cap extending lineWidth/2 past the endpoint.</summary>
    Square,
}

/// <summary>Join style between consecutive segments.</summary>
/// <remarks>Public for the same reason as <see cref="LineCap"/>.</remarks>
public enum LineJoin
{
    /// <summary>Extend the outer edges until they meet (capped by <c>miterLimit</c>).</summary>
    Miter,
    /// <summary>Arc join on the outer side.</summary>
    Round,
    /// <summary>Connect the outer edges with a straight line (bevel).</summary>
    Bevel,
}

/// <summary>
/// Converts an <see cref="IGlyphSink"/> path source into a stroked filled
/// outline that can be fed to any rasterizer.
///
/// <para>Strategy: collect all input segments, flatten curves to line segments
/// via the same recursive midpoint subdivision used by
/// <see cref="SmoothRasterizer"/>, then emit the stroke as a union of convex
/// pieces (a rectangle a segment, a piece a corner, a piece an open end), all
/// wound the same way. The output MUST be filled non-zero: the pieces overlap.</para>
///
/// <para>Stateless — every call allocates its own scratch. Thread-safe.</para>
/// </summary>
internal static class OutlineStroker
{
    /// <summary>
    /// Stroke the path described by <paramref name="drawTo"/> and emit the
    /// resulting filled outline to <paramref name="output"/>.
    /// </summary>
    /// <param name="drawTo">Callback that draws to an <see cref="IGlyphSink"/>.</param>
    /// <param name="output">Destination sink that receives the stroked outline.</param>
    /// <param name="lineWidth">Stroke width in the same units as the path.</param>
    /// <param name="cap">Cap style for open-contour endpoints.</param>
    /// <param name="join">Join style between consecutive segments.</param>
    /// <param name="miterLimit">
    /// Maximum miter length ratio. When the miter would exceed
    /// <c>miterLimit * lineWidth / 2</c> the join falls back to a bevel.
    /// </param>
    public static void Stroke(
        Action<IGlyphSink> drawTo,
        IGlyphSink output,
        float lineWidth,
        LineCap cap = LineCap.Butt,
        LineJoin join = LineJoin.Miter,
        float miterLimit = 4f)
    {
        if (drawTo is null || output is null || lineWidth <= 0f) return;

        // Step 1 – collect flat line segments organised by contour.
        var collector = new SegmentCollector();
        drawTo(collector);
        var contours = collector.Contours;

        var halfW = lineWidth * 0.5f;

        // Step 2 – stroke each contour as a UNION of simple convex pieces: a rectangle for every segment, a piece
        // for every corner and a piece for every open end. Each piece is emitted with the same orientation, so a
        // non-zero fill of all of them together is the stroke. Nothing has to work out where the two sides of the
        // stroke cross themselves on the inside of a corner, which is where an offset-curve stroker goes wrong.
        var points = new List<(float X, float Y)>();
        foreach (var contour in contours)
        {
            // Drop zero-length segments: they have no direction to offset along.
            points.Clear();
            foreach (var point in contour.Points)
            {
                if (points.Count == 0 || MathF.Abs(point.X - points[^1].X) > 1e-6f || MathF.Abs(point.Y - points[^1].Y) > 1e-6f)
                    points.Add(point);
            }
            var closed = contour.Closed;
            if (closed && points.Count > 1 && MathF.Abs(points[0].X - points[^1].X) <= 1e-6f && MathF.Abs(points[0].Y - points[^1].Y) <= 1e-6f)
                points.RemoveAt(points.Count - 1);

            var n = points.Count;
            if (n == 0) continue;
            if (n == 1)
            {
                // A subpath that goes nowhere still shows its caps: a dot, or a square.
                if (cap == LineCap.Round) EmitArc(points[0], halfW, 0f, MathF.PI * 2f, withCentre: false, output);
                else if (cap == LineCap.Square)
                    EmitPolygon([(points[0].X - halfW, points[0].Y - halfW), (points[0].X + halfW, points[0].Y - halfW),
                        (points[0].X + halfW, points[0].Y + halfW), (points[0].X - halfW, points[0].Y + halfW)], output);
                continue;
            }

            var segments = closed ? n : n - 1;
            for (var k = 0; k < segments; k++)
            {
                var a = points[k];
                var b = points[(k + 1) % n];
                Direction(a, b, out var dx, out var dy);
                var ox = -dy * halfW;
                var oy = dx * halfW;
                EmitPolygon([(a.X + ox, a.Y + oy), (b.X + ox, b.Y + oy), (b.X - ox, b.Y - oy), (a.X - ox, a.Y - oy)], output);

                // The corner at the END of this segment, unless that is an open end.
                if (!closed && k == segments - 1) break;
                var c = points[(k + 2) % n];
                Direction(b, c, out var ex, out var ey);
                EmitJoin(b, dx, dy, ex, ey, halfW, join, miterLimit, output);
            }

            if (!closed)
            {
                Direction(points[1], points[0], out var sx, out var sy);            // pointing out of the start
                EmitCap(points[0], sx, sy, halfW, cap, output);
                Direction(points[n - 2], points[n - 1], out var tx, out var ty);    // pointing out of the end
                EmitCap(points[n - 1], tx, ty, halfW, cap, output);
            }
        }
    }

    // ── Pieces ────────────────────────────────────────────────────────────────

    /// <summary>How far an arc piece may be from the arc it stands for, in the path's units.</summary>
    private const float ArcTolerance = 0.05f;

    private static void Direction((float X, float Y) from, (float X, float Y) to, out float dx, out float dy)
    {
        dx = to.X - from.X;
        dy = to.Y - from.Y;
        var length = MathF.Sqrt(dx * dx + dy * dy);
        if (length > 0f) { dx /= length; dy /= length; }
    }

    /// <summary>
    /// The piece that fills the outside of a corner at <paramref name="v"/>, between a segment arriving along
    /// (<paramref name="dx0"/>, <paramref name="dy0"/>) and one leaving along (<paramref name="dx1"/>, <paramref name="dy1"/>).
    /// </summary>
    private static void EmitJoin((float X, float Y) v, float dx0, float dy0, float dx1, float dy1,
        float halfW, LineJoin join, float miterLimit, IGlyphSink output)
    {
        var cross = dx0 * dy1 - dy0 * dx1;
        var dot = dx0 * dx1 + dy0 * dy1;
        if (MathF.Abs(cross) < 1e-6f)
        {
            // Straight on needs nothing. Doubling back has no outside to speak of, except that a round join is a
            // full half disc there.
            if (dot < 0f && join == LineJoin.Round) EmitArc(v, halfW, 0f, MathF.PI * 2f, withCentre: false, output);
            return;
        }

        // The outside of the corner is on the side the path turns away from.
        var side = cross > 0f ? -1f : 1f;
        var ox0 = -dy0 * halfW * side;
        var oy0 = dx0 * halfW * side;
        var ox1 = -dy1 * halfW * side;
        var oy1 = dx1 * halfW * side;

        if (join == LineJoin.Round)
        {
            var a0 = MathF.Atan2(oy0, ox0);
            var sweep = MathF.Atan2(oy1, ox1) - a0;
            // The short way round, which is the outside of the corner.
            while (sweep > MathF.PI) sweep -= MathF.PI * 2f;
            while (sweep < -MathF.PI) sweep += MathF.PI * 2f;
            EmitArc(v, halfW, a0, sweep, withCentre: true, output);
            return;
        }

        if (join == LineJoin.Miter)
        {
            // Miter length over stroke width is 1 / cos(half the turn) = sqrt(2 / (1 + cos turn)).
            var ratio = MathF.Sqrt(2f / MathF.Max(1e-12f, 1f + dot));
            if (ratio <= miterLimit)
            {
                var bx = ox0 + ox1;
                var by = oy0 + oy1;
                var length = MathF.Sqrt(bx * bx + by * by);
                var tip = (v.X + bx / length * halfW * ratio, v.Y + by / length * halfW * ratio);
                EmitPolygon([v, (v.X + ox0, v.Y + oy0), tip, (v.X + ox1, v.Y + oy1)], output);
                return;
            }
        }

        // Bevel, and a miter past its limit.
        EmitPolygon([v, (v.X + ox0, v.Y + oy0), (v.X + ox1, v.Y + oy1)], output);
    }

    /// <summary>The piece beyond an open end at <paramref name="p"/>, where (<paramref name="dx"/>, <paramref name="dy"/>) points out of the path.</summary>
    private static void EmitCap((float X, float Y) p, float dx, float dy, float halfW, LineCap cap, IGlyphSink output)
    {
        var ox = -dy * halfW;
        var oy = dx * halfW;
        if (cap == LineCap.Square)
        {
            EmitPolygon([(p.X + ox, p.Y + oy), (p.X + ox + dx * halfW, p.Y + oy + dy * halfW),
                (p.X - ox + dx * halfW, p.Y - oy + dy * halfW), (p.X - ox, p.Y - oy)], output);
        }
        else if (cap == LineCap.Round)
        {
            // Half a disc on the outward side: from one side of the end, round by the outward direction, to the other.
            var a0 = MathF.Atan2(oy, ox);
            var outward = MathF.Atan2(dy, dx) - a0;
            while (outward > MathF.PI) outward -= MathF.PI * 2f;
            while (outward < -MathF.PI) outward += MathF.PI * 2f;
            EmitArc(p, halfW, a0, outward > 0f ? MathF.PI : -MathF.PI, withCentre: false, output);
        }
    }

    /// <summary>
    /// A disc, or a slice of one with or without its centre, as a polygon cut finely enough to stay within
    /// <see cref="ArcTolerance"/> of the arc.
    /// </summary>
    private static void EmitArc((float X, float Y) centre, float radius, float start, float sweep, bool withCentre, IGlyphSink output)
    {
        // A chord over an angle t is r (1 - cos(t / 2)) from its arc.
        var step = radius > ArcTolerance ? 2f * MathF.Acos(1f - ArcTolerance / radius) : MathF.PI / 2f;
        var steps = Math.Clamp((int)MathF.Ceiling(MathF.Abs(sweep) / step), 2, 720);
        var full = MathF.Abs(sweep) >= MathF.PI * 2f - 1e-4f;
        var polygon = new List<(float X, float Y)>(steps + 2);
        if (withCentre && !full) polygon.Add(centre);
        var count = full ? steps : steps + 1;
        for (var k = 0; k < count; k++)
        {
            var a = start + sweep * k / steps;
            polygon.Add((centre.X + MathF.Cos(a) * radius, centre.Y + MathF.Sin(a) * radius));
        }
        EmitPolygon(polygon, output);
    }

    /// <summary>
    /// Emits a closed polygon, always wound the same way (positive signed area), whichever way its points were given.
    /// That is what makes the pieces a union under a non-zero fill, in a y-up font and in a y-down picture alike.
    /// </summary>
    private static void EmitPolygon(List<(float X, float Y)> pts, IGlyphSink sink)
    {
        var n = pts.Count;
        if (n < 3) return;
        var area = 0f;
        for (var k = 0; k < n; k++)
        {
            var a = pts[k];
            var b = pts[(k + 1) % n];
            area += a.X * b.Y - b.X * a.Y;
        }
        if (area == 0f) return;

        if (area > 0f)
        {
            sink.MoveTo(pts[0].X, pts[0].Y);
            for (var k = 1; k < n; k++) sink.LineTo(pts[k].X, pts[k].Y);
        }
        else
        {
            sink.MoveTo(pts[n - 1].X, pts[n - 1].Y);
            for (var k = n - 2; k >= 0; k--) sink.LineTo(pts[k].X, pts[k].Y);
        }
        sink.Close();
    }

    // ── Path collector ────────────────────────────────────────────────────────

    internal sealed class Contour
    {
        public List<(float X, float Y)> Points { get; } = [];
        public bool Closed { get; set; }
    }

    /// <summary>
    /// Receives raw IGlyphSink calls, flattens quadratic and cubic Béziers into
    /// line segments, and groups them by contour.
    /// </summary>
    internal sealed class SegmentCollector : IGlyphSink
    {
        private readonly List<Contour> _contours = [];
        private Contour? _current;
        private float _curX, _curY;

        public IReadOnlyList<Contour> Contours => _contours;

        public void MoveTo(float x, float y)
        {
            _current = new Contour();
            _contours.Add(_current);
            _current.Points.Add((x, y));
            _curX = x;
            _curY = y;
        }

        public void LineTo(float x, float y)
        {
            EnsureContour();
            _current!.Points.Add((x, y));
            _curX = x;
            _curY = y;
        }

        public void QuadTo(float cx, float cy, float x, float y)
        {
            EnsureContour();
            SubdivideQuad(_curX, _curY, cx, cy, x, y, depth: 0);
            _curX = x;
            _curY = y;
        }

        public void CubicTo(float c1x, float c1y, float c2x, float c2y, float x, float y)
        {
            EnsureContour();
            SubdivideCubic(_curX, _curY, c1x, c1y, c2x, c2y, x, y, depth: 0);
            _curX = x;
            _curY = y;
        }

        public void Close()
        {
            if (_current is null) return;
            // Close back to start if necessary.
            var first = _current.Points[0];
            if (MathF.Abs(_curX - first.X) > 1e-4f || MathF.Abs(_curY - first.Y) > 1e-4f)
                _current.Points.Add(first);
            _current.Closed = true;
            _current = null;
        }

        private void EnsureContour()
        {
            if (_current is null)
            {
                _current = new Contour();
                _contours.Add(_current);
            }
        }

        private void SubdivideQuad(float x0, float y0, float cx, float cy,
            float x1, float y1, int depth)
        {
            var dx = x1 - x0;
            var dy = y1 - y0;
            var cross = (cx - x0) * dy - (cy - y0) * dx;
            var lenSq = dx * dx + dy * dy;
            if (depth >= 16 || cross * cross <= 0.0625f * lenSq + 0.01f)
            {
                _current!.Points.Add((x1, y1));
                return;
            }
            var mx0 = (x0 + cx) * 0.5f;
            var my0 = (y0 + cy) * 0.5f;
            var mx1 = (cx + x1) * 0.5f;
            var my1 = (cy + y1) * 0.5f;
            var mx  = (mx0 + mx1) * 0.5f;
            var my  = (my0 + my1) * 0.5f;
            SubdivideQuad(x0, y0, mx0, my0, mx, my, depth + 1);
            SubdivideQuad(mx, my, mx1, my1, x1, y1, depth + 1);
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
                _current!.Points.Add((x1, y1));
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
    }
}

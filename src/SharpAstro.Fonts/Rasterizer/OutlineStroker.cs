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
/// <see cref="SmoothRasterizer"/>, then for each open/closed polyline build
/// the left and right offset polygons, apply end-caps, and emit them as
/// closed filled contours to the output sink.</para>
///
/// <para>Stateless — every call allocates its own scratch. Thread-safe.</para>
/// </summary>
internal static class OutlineStroker
{
    // Number of arc segments per full turn when approximating round caps/joins
    // with line segments. 24 gives a good quality/cost trade-off.
    private const int ArcSegments = 24;

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

        // Step 2 – stroke each contour.
        foreach (var contour in contours)
        {
            var pts = contour.Points;
            var closed = contour.Closed;
            var n = pts.Count;

            if (n < 2) continue; // degenerate — nothing to stroke

            if (closed)
                StrokeClosed(pts, halfW, join, miterLimit, output);
            else
                StrokeOpen(pts, halfW, cap, join, miterLimit, output);
        }
    }

    // ── Closed polyline stroking ──────────────────────────────────────────────

    private static void StrokeClosed(
        List<(float X, float Y)> pts,
        float halfW,
        LineJoin join,
        float miterLimit,
        IGlyphSink output)
    {
        // Build left (outer at winding >0) and right (inner) offset polylines.
        // Both lists end up as closed loops.
        var left  = new List<(float X, float Y)>();
        var right = new List<(float X, float Y)>();

        var n = pts.Count;
        for (var i = 0; i < n; i++)
        {
            var prev = pts[(i - 1 + n) % n];
            var curr = pts[i];
            var next = pts[(i + 1) % n];

            var dx0 = curr.X - prev.X;
            var dy0 = curr.Y - prev.Y;
            var dx1 = next.X - curr.X;
            var dy1 = next.Y - curr.Y;

            NormPerp(dx0, dy0, out var lx0, out var ly0);
            NormPerp(dx1, dy1, out var lx1, out var ly1);

            AddJoinPoints(
                curr.X, curr.Y,
                lx0, ly0, lx1, ly1,
                halfW, join, miterLimit,
                left, right);
        }

        // Emit left side as one closed contour, then right side reversed as
        // another closed contour (so the two together form the stroke band).
        EmitClosed(left, output);
        right.Reverse();
        EmitClosed(right, output);
    }

    // ── Open polyline stroking ────────────────────────────────────────────────

    private static void StrokeOpen(
        List<(float X, float Y)> pts,
        float halfW,
        LineCap cap,
        LineJoin join,
        float miterLimit,
        IGlyphSink output)
    {
        // Walk forward along the left side of the polyline then backward along
        // the right side, inserting caps at each end, to form one closed contour.
        var n = pts.Count;
        var outline = new List<(float X, float Y)>();

        // ── Forward pass (left side) ──────────────────────────────────────────
        for (var i = 0; i < n; i++)
        {
            var curr = pts[i];

            if (i == 0)
            {
                // Start cap — left side.
                var dx = pts[1].X - curr.X;
                var dy = pts[1].Y - curr.Y;
                NormPerp(dx, dy, out var px, out var py);
                var lx = curr.X + px * halfW;
                var ly = curr.Y + py * halfW;

                switch (cap)
                {
                    case LineCap.Butt:
                        outline.Add((lx, ly));
                        break;

                    case LineCap.Square:
                        // Extend backward by halfW in the -direction of travel.
                        var sqLen = MathF.Sqrt(dx * dx + dy * dy);
                        float tx, ty;
                        if (sqLen > 0f) { tx = dx / sqLen; ty = dy / sqLen; }
                        else { tx = 0; ty = 0; }
                        outline.Add((lx - tx * halfW, ly - ty * halfW));
                        break;

                    case LineCap.Round:
                        // Semicircle from right (-perp) to left (+perp) around the start.
                        AddArcPoints(curr.X, curr.Y, halfW,
                            MathF.Atan2(-py, -px),  // start angle = -perp direction (right side)
                            MathF.Atan2(py, px),     // end   angle = +perp direction (left  side)
                            ccw: true,
                            outline);
                        break;
                }
            }
            else if (i == n - 1)
            {
                // End cap — left side.
                var dx = curr.X - pts[i - 1].X;
                var dy = curr.Y - pts[i - 1].Y;
                NormPerp(dx, dy, out var px, out var py);
                var lx = curr.X + px * halfW;
                var ly = curr.Y + py * halfW;

                switch (cap)
                {
                    case LineCap.Butt:
                        outline.Add((lx, ly));
                        break;

                    case LineCap.Square:
                        var sqLen = MathF.Sqrt(dx * dx + dy * dy);
                        float tx, ty;
                        if (sqLen > 0f) { tx = dx / sqLen; ty = dy / sqLen; }
                        else { tx = 0; ty = 0; }
                        outline.Add((lx + tx * halfW, ly + ty * halfW));
                        break;

                    case LineCap.Round:
                        // Semicircle from left side (+perp) to right side (-perp).
                        AddArcPoints(curr.X, curr.Y, halfW,
                            MathF.Atan2(py, px),
                            MathF.Atan2(-py, -px),
                            ccw: true,
                            outline);
                        break;
                }
            }
            else
            {
                // Interior join — left side only.
                var prev = pts[i - 1];
                var next = pts[i + 1];

                var dx0 = curr.X - prev.X;
                var dy0 = curr.Y - prev.Y;
                var dx1 = next.X - curr.X;
                var dy1 = next.Y - curr.Y;

                NormPerp(dx0, dy0, out var lx0, out var ly0);
                NormPerp(dx1, dy1, out var lx1, out var ly1);

                var leftPts  = new List<(float X, float Y)>();
                var rightPts = new List<(float X, float Y)>();
                AddJoinPoints(curr.X, curr.Y, lx0, ly0, lx1, ly1,
                    halfW, join, miterLimit, leftPts, rightPts);

                foreach (var p in leftPts) outline.Add(p);
            }
        }

        // ── Backward pass (right side) ────────────────────────────────────────
        for (var i = n - 1; i >= 0; i--)
        {
            var curr = pts[i];

            if (i == n - 1)
            {
                // Transition from end-left to end-right already handled by the
                // cap above.  For Butt/Square we need to add the right-side
                // end point; for Round the arc already connected them.
                if (cap != LineCap.Round)
                {
                    var dx = curr.X - pts[i - 1].X;
                    var dy = curr.Y - pts[i - 1].Y;
                    NormPerp(dx, dy, out var px, out var py);
                    var rx = curr.X - px * halfW;
                    var ry = curr.Y - py * halfW;

                    if (cap == LineCap.Square)
                    {
                        var sqLen = MathF.Sqrt(dx * dx + dy * dy);
                        float tx, ty;
                        if (sqLen > 0f) { tx = dx / sqLen; ty = dy / sqLen; }
                        else { tx = 0; ty = 0; }
                        outline.Add((rx + tx * halfW, ry + ty * halfW));
                    }
                    else
                    {
                        outline.Add((rx, ry));
                    }
                }
            }
            else if (i == 0)
            {
                // Transition from start-right back to start-left; for Butt/Square
                // we close with the right-side start point.
                if (cap != LineCap.Round)
                {
                    var dx = pts[1].X - curr.X;
                    var dy = pts[1].Y - curr.Y;
                    NormPerp(dx, dy, out var px, out var py);
                    var rx = curr.X - px * halfW;
                    var ry = curr.Y - py * halfW;

                    if (cap == LineCap.Square)
                    {
                        var sqLen = MathF.Sqrt(dx * dx + dy * dy);
                        float tx, ty;
                        if (sqLen > 0f) { tx = dx / sqLen; ty = dy / sqLen; }
                        else { tx = 0; ty = 0; }
                        outline.Add((rx - tx * halfW, ry - ty * halfW));
                    }
                    else
                    {
                        outline.Add((rx, ry));
                    }
                }
            }
            else
            {
                // Interior join — right side (perpendicular reversed).
                var prev = pts[i + 1]; // "previous" in backward traversal = next in forward
                var next = pts[i - 1];

                // In backward traversal the segment directions are reversed.
                var dx0 = curr.X - prev.X;
                var dy0 = curr.Y - prev.Y;
                var dx1 = next.X - curr.X;
                var dy1 = next.Y - curr.Y;

                NormPerp(dx0, dy0, out var lx0, out var ly0);
                NormPerp(dx1, dy1, out var lx1, out var ly1);

                var leftPts  = new List<(float X, float Y)>();
                var rightPts = new List<(float X, float Y)>();
                AddJoinPoints(curr.X, curr.Y, lx0, ly0, lx1, ly1,
                    halfW, join, miterLimit, leftPts, rightPts);

                foreach (var p in leftPts) outline.Add(p);
            }
        }

        EmitClosed(outline, output);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Compute join geometry at a vertex. The left offset polygon receives
    /// points from <paramref name="left"/>, the right polygon from
    /// <paramref name="right"/>.
    /// </summary>
    private static void AddJoinPoints(
        float cx, float cy,
        float lx0, float ly0,   // unit left-normal of incoming segment
        float lx1, float ly1,   // unit left-normal of outgoing segment
        float halfW,
        LineJoin join,
        float miterLimit,
        List<(float X, float Y)> left,
        List<(float X, float Y)> right)
    {
        // Cross product to determine which side is the outer turn.
        var cross = lx0 * ly1 - ly0 * lx1;

        // Average (bisector) direction.
        var bx = lx0 + lx1;
        var by = ly0 + ly1;
        var bLen = MathF.Sqrt(bx * bx + by * by);
        float sinHalf;  // sin of half the interior angle
        if (bLen < 1e-6f)
        {
            // 180° turn — degenerate; use incoming normal only.
            sinHalf = 1f;
            bx = lx0; by = ly0; bLen = 1f;
        }
        else
        {
            // dot(bisector, lx0) = cos(half-angle), but we need its complement.
            sinHalf = MathF.Abs(cross * 0.5f); // approximation good enough for miter limit
            bx /= bLen; by /= bLen;
        }

        // Miter length = halfW / sin(half-angle) ≈ halfW / (|cross|/2)
        // but we protect against zero-cross.
        var miterLen = (MathF.Abs(cross) > 1e-6f) ? halfW / (MathF.Abs(cross) * 0.5f) : float.MaxValue;
        _ = sinHalf; // kept for documentation; miterLen supersedes it

        if (cross >= 0f)
        {
            // Left turn — outer side is left.
            if (join == LineJoin.Miter && miterLen <= miterLimit * halfW)
            {
                // Miter point is bisector offset.
                var mx = cx + bx * miterLen;
                var my = cy + by * miterLen;
                left.Add((mx, my));
            }
            else if (join == LineJoin.Round)
            {
                // Arc on the outer (left) side.
                var a0 = MathF.Atan2(ly0, lx0);
                var a1 = MathF.Atan2(ly1, lx1);
                AddArcPoints(cx, cy, halfW, a0, a1, ccw: true, left);
            }
            else
            {
                // Bevel (or miter fallback): two points.
                left.Add((cx + lx0 * halfW, cy + ly0 * halfW));
                left.Add((cx + lx1 * halfW, cy + ly1 * halfW));
            }
            // Inner (right) side — simple single intersection.
            right.Add((cx - bx * halfW, cy - by * halfW));
        }
        else
        {
            // Right turn — outer side is right.
            if (join == LineJoin.Miter && miterLen <= miterLimit * halfW)
            {
                var mx = cx - bx * miterLen;
                var my = cy - by * miterLen;
                right.Add((mx, my));
            }
            else if (join == LineJoin.Round)
            {
                var a0 = MathF.Atan2(-ly0, -lx0);
                var a1 = MathF.Atan2(-ly1, -lx1);
                AddArcPoints(cx, cy, halfW, a0, a1, ccw: false, right);
            }
            else
            {
                right.Add((cx - lx0 * halfW, cy - ly0 * halfW));
                right.Add((cx - lx1 * halfW, cy - ly1 * halfW));
            }
            left.Add((cx + bx * halfW, cy + by * halfW));
        }
    }

    /// <summary>
    /// Append arc sample points (excluding the exact start) from angle
    /// <paramref name="a0"/> to <paramref name="a1"/> around
    /// <c>(cx, cy)</c> with the given <paramref name="radius"/>.
    /// </summary>
    private static void AddArcPoints(
        float cx, float cy, float radius,
        float a0, float a1,
        bool ccw,
        List<(float X, float Y)> pts)
    {
        // Normalise the angular span to [0, 2π) in the requested direction.
        const float TwoPi = MathF.PI * 2f;
        var span = ccw ? a1 - a0 : a0 - a1;
        while (span < 0f) span += TwoPi;
        while (span > TwoPi) span -= TwoPi;

        var steps = Math.Max(1, (int)MathF.Ceiling(span / TwoPi * ArcSegments));
        var dAngle = (ccw ? span : -span) / steps;

        for (var k = 1; k <= steps; k++)
        {
            var a = a0 + dAngle * k;
            pts.Add((cx + MathF.Cos(a) * radius, cy + MathF.Sin(a) * radius));
        }
    }

    /// <summary>
    /// Compute the unit left-hand (CCW) perpendicular of vector <c>(dx, dy)</c>.
    /// Returns <c>(0, 0)</c> for zero-length input.
    /// </summary>
    private static void NormPerp(float dx, float dy, out float px, out float py)
    {
        var len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < 1e-9f) { px = 0f; py = 0f; return; }
        // Left perpendicular of (dx, dy) is (-dy, dx) normalised.
        px = -dy / len;
        py =  dx / len;
    }

    /// <summary>Emit a closed polygon from a point list.</summary>
    private static void EmitClosed(List<(float X, float Y)> pts, IGlyphSink sink)
    {
        if (pts.Count == 0) return;
        sink.MoveTo(pts[0].X, pts[0].Y);
        for (var i = 1; i < pts.Count; i++)
            sink.LineTo(pts[i].X, pts[i].Y);
        sink.Close();
    }

    // ── Path collector ────────────────────────────────────────────────────────

    private sealed class Contour
    {
        public List<(float X, float Y)> Points { get; } = [];
        public bool Closed { get; set; }
    }

    /// <summary>
    /// Receives raw IGlyphSink calls, flattens quadratic and cubic Béziers into
    /// line segments, and groups them by contour.
    /// </summary>
    private sealed class SegmentCollector : IGlyphSink
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

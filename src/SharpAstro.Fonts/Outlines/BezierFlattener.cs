namespace SharpAstro.Fonts.Outlines;

/// <summary>
/// Walks an <see cref="Outline"/> and emits MoveTo / LineTo / QuadTo / Close
/// to an <see cref="IGlyphSink"/>. Handles TrueType's "consecutive off-curve
/// points imply implicit on-curve midpoints" convention.
///
/// No allocation per call — only stack-local state.
/// </summary>
public static class BezierFlattener
{
    public static void Walk(Outline outline, IGlyphSink sink)
    {
        var x = outline.X;
        var y = outline.Y;
        var flags = outline.Flags;
        var ends = outline.ContourEnds;
        if (ends.Length == 0) return;

        var contourStart = 0;
        for (var ci = 0; ci < ends.Length; ci++)
        {
            var end = ends[ci];
            if (end < contourStart) { contourStart = end + 1; continue; }
            EmitContour(x, y, flags, contourStart, end, sink);
            contourStart = end + 1;
        }
    }

    private static void EmitContour(ReadOnlySpan<short> x, ReadOnlySpan<short> y,
        ReadOnlySpan<byte> flags, int start, int end, IGlyphSink sink)
    {
        // Determine the "first on-curve point". TrueType allows a contour to
        // begin with off-curve points; we synthesize a starting on-curve as the
        // midpoint of the first and last points if needed.
        var len = end - start + 1;
        if (len <= 0) return;

        bool firstOn = (flags[start] & 1) != 0;
        bool lastOn  = (flags[end]   & 1) != 0;

        float startX, startY;
        int firstIndex; // index after the synthetic start, where iteration begins
        if (firstOn)
        {
            startX = x[start];
            startY = y[start];
            firstIndex = start + 1;
        }
        else if (lastOn)
        {
            startX = x[end];
            startY = y[end];
            firstIndex = start;
            // Treat 'end' as already consumed by being our start point; the
            // loop will wrap and emit nothing extra at the end.
        }
        else
        {
            // Both endpoints are off-curve — start at the midpoint of first/last.
            startX = (x[start] + x[end]) * 0.5f;
            startY = (y[start] + y[end]) * 0.5f;
            firstIndex = start;
        }

        sink.MoveTo(startX, startY);

        // We walk the contour points starting at firstIndex, wrapping around to
        // the synthetic start. We carry a "pending control point" if the
        // previous point was off-curve.
        bool hasPending = false;
        float pendX = 0, pendY = 0;

        // Loop length = len when starting with synthetic midpoint, len-1 if we
        // already consumed an endpoint as the start. Easier: loop through every
        // contour point once relative to firstIndex modulo len.
        for (var step = 0; step < len; step++)
        {
            var idx = start + ((firstIndex - start + step) % len);
            var on = (flags[idx] & 1) != 0;
            float px = x[idx], py = y[idx];

            if (on)
            {
                if (hasPending)
                {
                    sink.QuadTo(pendX, pendY, px, py);
                    hasPending = false;
                }
                else
                {
                    sink.LineTo(px, py);
                }
            }
            else
            {
                if (hasPending)
                {
                    // Two off-curve in a row → implicit on-curve midpoint.
                    var mx = (pendX + px) * 0.5f;
                    var my = (pendY + py) * 0.5f;
                    sink.QuadTo(pendX, pendY, mx, my);
                }
                pendX = px;
                pendY = py;
                hasPending = true;
            }
        }

        // Close back to the start — flush any trailing control with a quad to
        // the original start point.
        if (hasPending)
            sink.QuadTo(pendX, pendY, startX, startY);
        sink.Close();
    }
}

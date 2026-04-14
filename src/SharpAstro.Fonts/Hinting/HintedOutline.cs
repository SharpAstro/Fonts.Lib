using SharpAstro.Fonts.Outlines;

namespace SharpAstro.Fonts.Hinting;

/// <summary>
/// A glyph outline whose points have been scaled to pixel units and (where
/// the font carries hinting bytecode) snapped to the pixel grid by the
/// TrueType interpreter.
///
/// <para>Coordinates are stored as F26.6 (1/64 pixel). Phantom points are
/// stripped before construction — only the visible glyph contour points
/// remain.</para>
///
/// <para>Immutable; safe to share across threads.</para>
/// </summary>
public sealed class HintedOutline
{
    /// <summary>F26.6 X coordinates, pixels.</summary>
    private readonly int[] _x;
    /// <summary>F26.6 Y coordinates, pixels.</summary>
    private readonly int[] _y;
    /// <summary>Bit 0 = on-curve.</summary>
    private readonly byte[] _flags;
    /// <summary>Inclusive end indices for each contour.</summary>
    private readonly int[] _contourEnds;

    public HintedOutline(int[] x, int[] y, byte[] flags, int[] contourEnds)
    {
        _x = x;
        _y = y;
        _flags = flags;
        _contourEnds = contourEnds;
    }

    public static readonly HintedOutline Empty = new([], [], [], []);

    public bool IsEmpty => _contourEnds.Length == 0;
    public int PointCount => _x.Length;
    public int ContourCount => _contourEnds.Length;

    public ReadOnlySpan<int> X => _x;
    public ReadOnlySpan<int> Y => _y;
    public ReadOnlySpan<byte> Flags => _flags;
    public ReadOnlySpan<int> ContourEnds => _contourEnds;

    public bool IsOnCurve(int pointIndex) => (_flags[pointIndex] & 1) != 0;

    /// <summary>
    /// Walk this outline as path commands, emitting coordinates in floating
    /// pixel units (F26.6 / 64). Mirrors <see cref="BezierFlattener.Walk"/>
    /// semantics for implicit on-curve midpoints + all-off-curve contours.
    /// </summary>
    public void Walk(IGlyphSink sink)
    {
        const float K = 1f / 64f;
        if (_contourEnds.Length == 0) return;
        var contourStart = 0;
        for (var ci = 0; ci < _contourEnds.Length; ci++)
        {
            var end = _contourEnds[ci];
            if (end < contourStart) { contourStart = end + 1; continue; }
            EmitContour(sink, contourStart, end, K);
            contourStart = end + 1;
        }
    }

    private void EmitContour(IGlyphSink sink, int start, int end, float k)
    {
        var len = end - start + 1;
        if (len <= 0) return;

        bool firstOn = (_flags[start] & 1) != 0;
        bool lastOn  = (_flags[end]   & 1) != 0;

        float startX, startY;
        int firstIndex;
        if (firstOn)
        {
            startX = _x[start] * k;
            startY = _y[start] * k;
            firstIndex = start + 1;
        }
        else if (lastOn)
        {
            startX = _x[end] * k;
            startY = _y[end] * k;
            firstIndex = start;
        }
        else
        {
            startX = (_x[start] + _x[end]) * 0.5f * k;
            startY = (_y[start] + _y[end]) * 0.5f * k;
            firstIndex = start;
        }

        sink.MoveTo(startX, startY);

        bool hasPending = false;
        float pendX = 0, pendY = 0;

        for (var step = 0; step < len; step++)
        {
            var idx = start + ((firstIndex - start + step) % len);
            var on = (_flags[idx] & 1) != 0;
            float px = _x[idx] * k, py = _y[idx] * k;

            if (on)
            {
                if (hasPending)
                {
                    sink.QuadTo(pendX, pendY, px, py);
                    hasPending = false;
                }
                else sink.LineTo(px, py);
            }
            else
            {
                if (hasPending)
                {
                    var mx = (pendX + px) * 0.5f;
                    var my = (pendY + py) * 0.5f;
                    sink.QuadTo(pendX, pendY, mx, my);
                }
                pendX = px; pendY = py;
                hasPending = true;
            }
        }

        if (hasPending)
            sink.QuadTo(pendX, pendY, startX, startY);
        sink.Close();
    }
}

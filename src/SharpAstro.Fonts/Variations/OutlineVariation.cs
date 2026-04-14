using System.Buffers;
using System.Runtime.InteropServices;
using SharpAstro.Fonts.Outlines;
using SharpAstro.Fonts.Tables.Gvar;

namespace SharpAstro.Fonts.Variations;

/// <summary>
/// Apply gvar tuple variations to a base TrueType outline at the given
/// normalized variation coordinates. Implements IUP (Interpolation of
/// Untouched Points) per the OpenType spec for the case where only a
/// subset of the glyph's points are explicitly affected by a tuple.
///
/// <para>Stateless / per-call. Scratch arrays are rented from
/// <see cref="ArrayPool{T}"/> and returned after use. The only
/// per-call allocation is the resulting <see cref="Outline"/>.</para>
/// </summary>
internal static class OutlineVariation
{
    /// <summary>
    /// Returns a variated copy of <paramref name="outline"/>, or the same
    /// outline unchanged if there are no applicable tuples.
    /// </summary>
    public static Outline Apply(Outline outline, GvarTable gvar, uint glyphId,
        ReadOnlySpan<float> normalizedCoords)
    {
        if (outline.IsEmpty || !gvar.HasDataForGlyph(glyphId))
            return outline;

        var pointCount = outline.PointCount;
        var tuples = gvar.LoadGlyphTuples(glyphId, pointCount);
        if (tuples.Count == 0) return outline;

        // Rent scratch buffers from the pool instead of allocating.
        var deltaX = ArrayPool<float>.Shared.Rent(pointCount);
        var deltaY = ArrayPool<float>.Shared.Rent(pointCount);
        var touched = ArrayPool<bool>.Shared.Rent(pointCount);
        var tupleDx = ArrayPool<float>.Shared.Rent(pointCount);
        var tupleDy = ArrayPool<float>.Shared.Rent(pointCount);
        var tupleTouched = ArrayPool<bool>.Shared.Rent(pointCount);
        try
        {
            // Rented arrays may contain stale data — clear the slices we use.
            Array.Clear(deltaX, 0, pointCount);
            Array.Clear(deltaY, 0, pointCount);
            Array.Clear(touched, 0, pointCount);

            foreach (var t in tuples)
            {
                var s = t.ComputeScalar(normalizedCoords);
                if (s == 0) continue;

                Array.Clear(tupleDx, 0, pointCount);
                Array.Clear(tupleDy, 0, pointCount);
                Array.Clear(tupleTouched, 0, pointCount);

                // Scatter explicit deltas (stop at pointCount — phantoms ignored).
                if (t.PointNumbers is null)
                {
                    // All-points tuple. The arrays may also include 4 phantom
                    // points after the outline points; just take the first
                    // pointCount entries.
                    var n = Math.Min(pointCount, t.DeltaX.Length);
                    for (var i = 0; i < n; i++)
                    {
                        tupleDx[i] = t.DeltaX[i];
                        tupleDy[i] = t.DeltaY[i];
                        tupleTouched[i] = true;
                    }
                }
                else
                {
                    var n = Math.Min(t.PointNumbers.Length, t.DeltaX.Length);
                    for (var i = 0; i < n; i++)
                    {
                        var p = t.PointNumbers[i];
                        if ((uint)p < (uint)pointCount)
                        {
                            tupleDx[p] = t.DeltaX[i];
                            tupleDy[p] = t.DeltaY[i];
                            tupleTouched[p] = true;
                        }
                    }
                    // IUP for untouched points.
                    ApplyIup(outline, tupleDx, tupleDy, tupleTouched);
                }

                // Accumulate scaled into the running delta.
                for (var i = 0; i < pointCount; i++)
                {
                    deltaX[i] += s * tupleDx[i];
                    deltaY[i] += s * tupleDy[i];
                    if (tupleTouched[i]) touched[i] = true;
                }
            }

            // Build new outline with rounded short coords.
            var srcX = outline.X;
            var srcY = outline.Y;
            var newX = new short[pointCount];
            var newY = new short[pointCount];
            for (var i = 0; i < pointCount; i++)
            {
                newX[i] = ClampShort(srcX[i] + deltaX[i]);
                newY[i] = ClampShort(srcY[i] + deltaY[i]);
            }
            // Rough updated bbox: scan the new points (cheaper than rerunning
            // bezier flatten, conservative enough for the rasterizer's bbox path).
            short xMin = short.MaxValue, yMin = short.MaxValue;
            short xMax = short.MinValue, yMax = short.MinValue;
            for (var i = 0; i < pointCount; i++)
            {
                if (newX[i] < xMin) xMin = newX[i];
                if (newX[i] > xMax) xMax = newX[i];
                if (newY[i] < yMin) yMin = newY[i];
                if (newY[i] > yMax) yMax = newY[i];
            }
            // Variation does not modify flags or contour ends — share the
            // original immutable arrays (zero-copy).
            return new Outline(
                ImmutableCollectionsMarshal.AsImmutableArray(newX),
                ImmutableCollectionsMarshal.AsImmutableArray(newY),
                outline.FlagsImmutable,
                outline.ContourEndsImmutable,
                (xMin, yMin, xMax, yMax));
        }
        finally
        {
            ArrayPool<float>.Shared.Return(deltaX);
            ArrayPool<float>.Shared.Return(deltaY);
            ArrayPool<bool>.Shared.Return(touched);
            ArrayPool<float>.Shared.Return(tupleDx);
            ArrayPool<float>.Shared.Return(tupleDy);
            ArrayPool<bool>.Shared.Return(tupleTouched);
        }
    }

    /// <summary>
    /// IUP per OpenType spec: for each contour, untouched points are linearly
    /// interpolated between the nearest preceding and following touched
    /// points, separately along X and Y axes.
    /// </summary>
    private static void ApplyIup(Outline outline, float[] dx, float[] dy, bool[] touched)
    {
        var ends = outline.ContourEnds;
        var x = outline.X;
        var y = outline.Y;

        var contourStart = 0;
        for (var ci = 0; ci < ends.Length; ci++)
        {
            var end = ends[ci];
            InterpolateContour(x, dx, touched, contourStart, end);
            InterpolateContour(y, dy, touched, contourStart, end);
            contourStart = end + 1;
        }
    }

    private static void InterpolateContour(ReadOnlySpan<short> coord, float[] delta,
        bool[] touched, int start, int end)
    {
        var n = end - start + 1;
        if (n <= 0) return;

        // Count touched.
        var touchedCount = 0;
        for (var i = start; i <= end; i++)
            if (touched[i]) touchedCount++;
        if (touchedCount == 0 || touchedCount == n) return; // nothing to do

        // For each run of untouched points between two touched points
        // (wrapping around the contour), interpolate.
        // Find first touched in the contour.
        var firstTouched = -1;
        for (var i = start; i <= end; i++)
            if (touched[i]) { firstTouched = i; break; }
        if (firstTouched < 0) return;

        var prev = firstTouched;
        for (var step = 1; step <= n; step++)
        {
            var idx = start + (firstTouched - start + step) % n;
            if (touched[idx])
            {
                if (idx != prev)
                    FillBetween(coord, delta, prev, idx, start, end);
                prev = idx;
            }
        }
    }

    private static void FillBetween(ReadOnlySpan<short> coord, float[] delta,
        int prev, int next, int start, int end)
    {
        var prevCoord = coord[prev];
        var nextCoord = coord[next];
        var prevDelta = delta[prev];
        var nextDelta = delta[next];
        var n = end - start + 1;

        // Walk from prev to next exclusively, with wraparound.
        var i = prev;
        while (true)
        {
            i++;
            if (i > end) i = start;
            if (i == next) break;

            var c = coord[i];
            float d;
            if (prevCoord == nextCoord)
            {
                // Degenerate: both anchors at the same coord.
                d = prevDelta == nextDelta ? prevDelta : 0f;
            }
            else
            {
                var minA = MathF.Min(prevCoord, nextCoord);
                var maxA = MathF.Max(prevCoord, nextCoord);
                if (c >= minA && c <= maxA)
                {
                    var t = (c - prevCoord) / (float)(nextCoord - prevCoord);
                    d = prevDelta + t * (nextDelta - prevDelta);
                }
                else
                {
                    // Outside the anchor range — shift by the closer anchor's delta.
                    d = c < minA == prevCoord < nextCoord ? prevDelta : nextDelta;
                }
            }
            delta[i] = d;
        }
        _ = n;
    }

    private static short ClampShort(float v)
    {
        if (v <= short.MinValue) return short.MinValue;
        if (v >= short.MaxValue) return short.MaxValue;
        return (short)MathF.Round(v);
    }
}

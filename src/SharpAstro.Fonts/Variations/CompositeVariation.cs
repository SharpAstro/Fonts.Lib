using SharpAstro.Fonts.Tables.Gvar;

namespace SharpAstro.Fonts.Variations;

/// <summary>
/// Applies gvar variation deltas to composite-glyph component offsets.
///
/// <para>For composite TrueType glyphs, gvar stores <em>component-anchor</em>
/// deltas rather than per-point outline deltas. Each component is treated as a
/// pseudo-point pair (x, y) in the gvar stream, where x and y are the
/// translation offsets (arg1, arg2) of that component. This class extracts
/// those deltas so <see cref="Outlines.CompositeGlyphParser"/> can apply them
/// during assembly.</para>
///
/// <para>Spec reference:
/// https://learn.microsoft.com/typography/opentype/spec/gvar §"Composite glyphs"
/// </para>
/// </summary>
internal static class CompositeVariation
{
    /// <summary>
    /// Compute per-component translation deltas for a composite glyph by
    /// evaluating its gvar tuples against <paramref name="normalizedCoords"/>.
    ///
    /// Returns an array of length <c>componentCount</c> where element [i] is
    /// the (dx, dy) to add to the i-th component's arg1/arg2 offsets. Returns
    /// null when no gvar data exists for the glyph or there are no components.
    /// </summary>
    public static (float Dx, float Dy)[]? GetComponentDeltas(
        GvarTable gvar, uint glyphId, ReadOnlySpan<float> normalizedCoords,
        int componentCount)
    {
        if (componentCount <= 0) return null;
        if (!gvar.HasDataForGlyph(glyphId)) return null;

        // For composite glyphs, gvar uses componentCount as the "pointCount" so
        // that it can encode one (dx, dy) delta pair per component. Four phantom
        // points follow, but we discard those.
        var tuples = gvar.LoadGlyphTuples(glyphId, componentCount);
        if (tuples.Count == 0) return null;

        var dxAcc = new float[componentCount];
        var dyAcc = new float[componentCount];

        foreach (var t in tuples)
        {
            var s = t.ComputeScalar(normalizedCoords);
            if (s == 0f) continue;

            if (t.PointNumbers is null)
            {
                // All-components tuple: first componentCount deltas map to components.
                var n = Math.Min(componentCount, t.DeltaX.Length);
                for (var i = 0; i < n; i++)
                {
                    dxAcc[i] += s * t.DeltaX[i];
                    dyAcc[i] += s * t.DeltaY[i];
                }
            }
            else
            {
                // Sparse component tuple.
                var n = Math.Min(t.PointNumbers.Length, t.DeltaX.Length);
                for (var i = 0; i < n; i++)
                {
                    var ci = t.PointNumbers[i];
                    if ((uint)ci < (uint)componentCount)
                    {
                        dxAcc[ci] += s * t.DeltaX[i];
                        dyAcc[ci] += s * t.DeltaY[i];
                    }
                }
            }
        }

        var result = new (float Dx, float Dy)[componentCount];
        for (var i = 0; i < componentCount; i++)
            result[i] = (dxAcc[i], dyAcc[i]);
        return result;
    }
}

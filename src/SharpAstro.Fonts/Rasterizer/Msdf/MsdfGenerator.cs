namespace SharpAstro.Fonts.Rasterizer.Msdf;

/// <summary>
/// Maps a cell texel to a point in the glyph's own (y-up) space:
/// <c>p = (BoxLeft + (px+0.5)·s, BoxTop − (py+0.5)·s)</c> where <c>s</c> is the
/// distance units per texel. Distances come out in the shape's own units and are
/// scaled to the field range at quantization time, so no per-glyph shape
/// transform is needed.
/// </summary>
internal readonly record struct CellProjection(double BoxLeft, double BoxTop, double UnitsPerTexel)
{
    public Vector2D Project(double px, double py) =>
        new(BoxLeft + (px + 0.5) * UnitsPerTexel, BoxTop - (py + 0.5) * UnitsPerTexel);
}

/// <summary>
/// The software MTSDF rasterizer (msdfgen algorithm). Each contour is sampled
/// into its own per-channel selector and the results are merged with a
/// winding-aware overlapping-contour combiner, so glyphs built from overlapping
/// shapes (e.g. a stem plus separate arm rectangles) don't get false interior
/// edges.
/// </summary>
internal static class MsdfGenerator
{
    /// <summary>
    /// Generate an MTSDF into <paramref name="pixels"/> (row-major, top-down,
    /// four floats R,G,B,A per texel). RGB carry the multi-channel signed
    /// pseudo-distance; A carries the true signed distance. Distances are in the
    /// shape's own units, scaled by <c>1/rangeUnits</c> and biased by +0.5, so a
    /// value of 0.5 sits on the outline and &gt;0.5 is inside.
    /// </summary>
    public static void Generate(
        Shape shape, int width, int height, double rangeUnits, CellProjection projection, float[] pixels)
    {
        var invRange = 1.0 / rangeUnits;
        var windings = ComputeWindings(shape);

        for (var py = 0; py < height; py++)
            GenerateRow(shape, windings, pixels, width, py, invRange, projection);

        CorrectPolarity(shape, pixels, width, height, projection);
        ErrorCorrect(shape, pixels, width, height, projection);
    }

    /// <summary>+1 for a clockwise (filled, in TrueType y-up) contour, −1 for counter-clockwise (a hole).</summary>
    private static int[] ComputeWindings(Shape shape)
    {
        var windings = new int[shape.Contours.Count];
        for (var i = 0; i < shape.Contours.Count; i++)
        {
            var area = 0.0;
            foreach (var edge in shape.Contours[i].Edges)
                area += Vector2D.Cross(edge.Point(0), edge.Point(1));
            windings[i] = area < 0 ? 1 : area > 0 ? -1 : 0;
        }

        return windings;
    }

    private static void GenerateRow(
        Shape shape, int[] windings, float[] pixels, int width, int py, double invRange, CellProjection projection)
    {
        var samplers = new MultiDistanceSampler[shape.Contours.Count];
        var rowBase = py * width * 4;
        for (var px = 0; px < width; px++)
        {
            var p = projection.Project(px, py);
            var value = Combine(shape, windings, samplers, p, invRange);
            var o = rowBase + px * 4;
            pixels[o] = value.R;
            pixels[o + 1] = value.G;
            pixels[o + 2] = value.B;
            pixels[o + 3] = value.A;
        }
    }

    private static (float R, float G, float B, float A) Combine(
        Shape shape, int[] windings, MultiDistanceSampler[] samplers, Vector2D p, double invRange)
    {
        var n = shape.Contours.Count;
        for (var i = 0; i < n; i++)
        {
            var sampler = new MultiDistanceSampler();
            var edges = shape.Contours[i].Edges;
            var m = edges.Count;
            for (var j = 0; j < m; j++)
                sampler.AddEdge(edges[(j - 1 + m) % m], edges[j], edges[(j + 1) % m], p);
            samplers[i] = sampler;
        }

        if (n == 1)
            return samplers[0].Resolve(p, invRange);

        // msdfgen's overlapping-contour combiner: merge contours by winding, then pick the contour responsible
        // for the nearest boundary of the union/difference so internal (overlapped) edges don't leak through.
        var shapeSel = new MultiDistanceSampler();
        var innerSel = new MultiDistanceSampler();
        var outerSel = new MultiDistanceSampler();
        for (var i = 0; i < n; i++)
        {
            var med = samplers[i].MedianDistance(p);
            shapeSel.Merge(samplers[i]);
            if (windings[i] > 0 && med >= 0)
                innerSel.Merge(samplers[i]);
            if (windings[i] < 0 && med <= 0)
                outerSel.Merge(samplers[i]);
        }

        var shapeMed = shapeSel.MedianDistance(p);
        var innerMed = innerSel.MedianDistance(p);
        var outerMed = outerSel.MedianDistance(p);

        MultiDistanceSampler chosen;
        double chosenMed;
        int winding;

        if (innerMed >= 0 && Math.Abs(innerMed) <= Math.Abs(outerMed))
        {
            chosen = innerSel;
            chosenMed = innerMed;
            winding = 1;
            for (var i = 0; i < n; i++)
            {
                if (windings[i] <= 0)
                    continue;
                var cd = samplers[i].MedianDistance(p);
                if (Math.Abs(cd) < Math.Abs(outerMed) && cd > chosenMed)
                {
                    chosen = samplers[i];
                    chosenMed = cd;
                }
            }
        }
        else if (outerMed <= 0 && Math.Abs(outerMed) <= Math.Abs(innerMed))
        {
            chosen = outerSel;
            chosenMed = outerMed;
            winding = -1;
            for (var i = 0; i < n; i++)
            {
                if (windings[i] >= 0)
                    continue;
                var cd = samplers[i].MedianDistance(p);
                if (Math.Abs(cd) < Math.Abs(innerMed) && cd < chosenMed)
                {
                    chosen = samplers[i];
                    chosenMed = cd;
                }
            }
        }
        else
        {
            return shapeSel.Resolve(p, invRange);
        }

        for (var i = 0; i < n; i++)
        {
            if (windings[i] == winding)
                continue;
            var cd = samplers[i].MedianDistance(p);
            if (cd * chosenMed >= 0 && Math.Abs(cd) < Math.Abs(chosenMed))
            {
                chosen = samplers[i];
                chosenMed = cd;
            }
        }

        if (chosenMed == shapeMed)
            chosen = shapeSel;
        return chosen.Resolve(p, invRange);
    }

    /// <summary>
    /// Force "inside ⇒ value &gt; 0.5". The per-edge sign follows the font's
    /// contour winding (CW-outer for TrueType, CCW-outer for CFF), so the overall
    /// polarity can come out inverted. Vote median-sign vs the winding-rule
    /// inside/outside test at clear (non-edge) sample texels and flip the whole
    /// cell if the majority disagree.
    /// </summary>
    private static void CorrectPolarity(Shape shape, float[] pixels, int width, int height, CellProjection projection)
    {
        var mismatches = 0;
        var matches = 0;
        var step = Math.Max(1, Math.Min(width, height) / 16);

        for (var py = step / 2; py < height; py += step)
        {
            for (var px = step / 2; px < width; px += step)
            {
                var o = (py * width + px) * 4;
                var median = Median(pixels[o], pixels[o + 1], pixels[o + 2]);
                if (Math.Abs(median - 0.5f) < 0.25f) // skip the antialiased boundary band
                    continue;
                var insideByField = median > 0.5f;
                var insideByWinding = shape.WindingAt(projection.Project(px, py)) != 0;
                if (insideByField == insideByWinding)
                    matches++;
                else
                    mismatches++;
            }
        }

        if (mismatches > matches)
            for (var i = 0; i < pixels.Length; i++)
                pixels[i] = 1f - pixels[i];
    }

    /// <summary>
    /// Error correction against the glyph's <em>exact</em> fill (the scanline
    /// nonzero-winding test) rather than the reconstructed alpha — because the
    /// overlapping-contour combiner can produce a wrong-signed <em>true</em>
    /// distance in a thin region near a sharp concave/reversal corner, where the
    /// field reports outside well inside the ink. An accurate field's sign at a
    /// texel centre always matches the exact fill there, so a disagreement marks
    /// an artifact: reflect the true-distance channel across 0.5 to the correct
    /// side (which preserves its antialiasing magnitude) and collapse RGB onto
    /// it. Genuine edges and sharp corners agree in sign and are left untouched,
    /// so multi-channel sharpening survives everywhere it is real.
    /// </summary>
    private static void ErrorCorrect(Shape shape, float[] px, int width, int height, CellProjection projection)
    {
        for (var py = 0; py < height; py++)
        {
            for (var pxi = 0; pxi < width; pxi++)
            {
                var o = (py * width + pxi) * 4;
                var insideExact = shape.WindingAt(projection.Project(pxi, py)) != 0;

                // Reflect the true-distance channel to the exact-fill side (1 - v flips across 0.5, keeping |v - 0.5|).
                if (px[o + 3] > 0.5f != insideExact)
                    px[o + 3] = 1f - px[o + 3];

                var median = Median(px[o], px[o + 1], px[o + 2]);
                if (median > 0.5f != insideExact)
                {
                    var trueValue = px[o + 3];
                    px[o] = trueValue;
                    px[o + 1] = trueValue;
                    px[o + 2] = trueValue;
                }
            }
        }
    }

    private static float Median(float a, float b, float c) => Math.Max(Math.Min(a, b), Math.Min(Math.Max(a, b), c));
}

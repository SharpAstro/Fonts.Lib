namespace SharpAstro.Fonts.Rasterizer.Msdf;

/// <summary>
/// Assigns msdfgen edge colours so the multi-channel field keeps sharp corners.
/// A faithful port of msdfgen's <c>edgeColoringSimple</c>: detect corners by the
/// angle between incident edge directions, then walk each contour assigning a
/// colour run per smooth spline so that the two edges meeting at a corner differ
/// in ≥1 channel.
/// </summary>
internal static class EdgeColoring
{
    /// <summary>Colour every contour of <paramref name="shape"/>. <paramref name="angleThreshold"/> is in radians.</summary>
    public static void ColorSimple(Shape shape, double angleThreshold = 3.0, ulong seed = 0)
    {
        var crossThreshold = Math.Sin(angleThreshold);
        var corners = new List<int>();

        foreach (var contour in shape.Contours)
        {
            var edges = contour.Edges;
            corners.Clear();
            if (edges.Count == 0)
                continue;

            // Identify corners: indices where the contour's tangent turns sharply.
            var prevDirection = edges[^1].Direction(1);
            for (var index = 0; index < edges.Count; index++)
            {
                var edge = edges[index];
                if (IsCorner(prevDirection.Normalize(), edge.Direction(0).Normalize(), crossThreshold))
                    corners.Add(index);
                prevDirection = edge.Direction(1);
            }

            if (corners.Count == 0)
            {
                // Fully smooth contour (e.g. an O): one colour everywhere.
                foreach (var edge in edges)
                    edge.Color = EdgeColor.White;
            }
            else if (corners.Count == 1)
            {
                ColorTeardrop(edges, corners[0], ref seed);
            }
            else
            {
                ColorMultiCorner(edges, corners, ref seed);
            }
        }
    }

    private static bool IsCorner(Vector2D aDir, Vector2D bDir, double crossThreshold) =>
        Vector2D.Dot(aDir, bDir) <= 0 || Math.Abs(Vector2D.Cross(aDir, bDir)) > crossThreshold;

    private static void ColorTeardrop(List<EdgeSegment> edges, int corner, ref ulong seed)
    {
        Span<EdgeColor> colors = [EdgeColor.White, EdgeColor.White, EdgeColor.White];
        SwitchColor(ref colors[0], ref seed);
        colors[2] = colors[0];
        SwitchColor(ref colors[2], ref seed);

        var m = edges.Count;
        if (m >= 3)
        {
            for (var i = 0; i < m; i++)
            {
                // msdfgen's banding of i∈[0,m) into the three colours {colors[0..2]}.
                var band = (int)(3 + 2.875 * i / (m - 1) - 1.4375 + 0.5) - 3;
                edges[(corner + i) % m].Color = colors[band + 1];
            }
        }
        else
        {
            // Degenerate single-corner contour with fewer than three edges: a uniform colour is a safe field
            // (the rare lone corner loses multi-channel sharpening). Splitting into thirds is left for later.
            foreach (var edge in edges)
                edge.Color = colors[0];
        }
    }

    private static void ColorMultiCorner(List<EdgeSegment> edges, List<int> corners, ref ulong seed)
    {
        var cornerCount = corners.Count;
        var spline = 0;
        var start = corners[0];
        var m = edges.Count;
        var color = EdgeColor.White;
        SwitchColor(ref color, ref seed);
        var initialColor = color;
        for (var i = 0; i < m; i++)
        {
            var index = (start + i) % m;
            if (spline + 1 < cornerCount && corners[spline + 1] == index)
            {
                spline++;
                SwitchColor(ref color, ref seed, spline == cornerCount - 1 ? initialColor : EdgeColor.Black);
            }

            edges[index].Color = color;
        }
    }

    private static void SwitchColor(ref EdgeColor color, ref ulong seed, EdgeColor banned = EdgeColor.Black)
    {
        var combined = color & banned;
        if (combined is EdgeColor.Red or EdgeColor.Green or EdgeColor.Blue)
        {
            color = combined ^ EdgeColor.White;
            return;
        }

        if (color is EdgeColor.Black or EdgeColor.White)
        {
            Span<EdgeColor> startColors = [EdgeColor.Cyan, EdgeColor.Magenta, EdgeColor.Yellow];
            color = startColors[(int)(seed % 3)];
            seed /= 3;
            return;
        }

        var shifted = (int)color << (int)(1 + (seed & 1));
        color = (EdgeColor)((shifted | (shifted >> 3)) & (int)EdgeColor.White);
        seed >>= 1;
    }
}

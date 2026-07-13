using SharpAstro.Fonts.Rasterizer;
using SharpAstro.Fonts.Rasterizer.Msdf;

namespace SharpAstro.Fonts.Tests;

/// <summary>
/// Guards <see cref="Shape.WindingAt"/> against the joint double-count: font outlines split
/// curves at their extrema, so every round glyph has curve segments whose shared joint lies at
/// an x-extremum with a vertical tangent. A horizontal ray through such a joint used to collect
/// one root from each adjacent segment (t≈1 and t≈0, both inside the inclusive [0,1] filter),
/// counting the single crossing twice — inverting the winding for the rest of the scanline.
/// ErrorCorrect then baked that inversion into the MTSDF as a one-row stripe of phantom ink,
/// rendering as detached gray dashes hugging the bottom curves of o/c/e/g/b at certain sizes.
/// The fix counts crossings per y-monotone piece with the half-open [low, high) interval rule,
/// which also makes tangent touches (y-extremum joints) net exactly zero.
/// </summary>
public class WindingRayTests
{
    // A unit "circle" of four quadratic arcs, CCW, with joints at E(1,0), N(0,1), W(-1,0), S(0,-1).
    // E/W are x-extrema (vertical tangent, transversal crossing); N/S are y-extrema (tangent touch).
    private static Shape QuadCircle()
    {
        var c = new Contour();
        c.Add(new QuadraticSegment(new Vector2D(1, 0), new Vector2D(1, 1), new Vector2D(0, 1)));
        c.Add(new QuadraticSegment(new Vector2D(0, 1), new Vector2D(-1, 1), new Vector2D(-1, 0)));
        c.Add(new QuadraticSegment(new Vector2D(-1, 0), new Vector2D(-1, -1), new Vector2D(0, -1)));
        c.Add(new QuadraticSegment(new Vector2D(0, -1), new Vector2D(1, -1), new Vector2D(1, 0)));
        var s = new Shape();
        s.Contours.Add(c);
        return s;
    }

    // Same shape from cubic arcs (kappa control points), exercising CubicSegment's path.
    private static Shape CubicCircle()
    {
        const double k = 0.5522847498307936;
        var c = new Contour();
        c.Add(new CubicSegment(new Vector2D(1, 0), new Vector2D(1, k), new Vector2D(k, 1), new Vector2D(0, 1)));
        c.Add(new CubicSegment(new Vector2D(0, 1), new Vector2D(-k, 1), new Vector2D(-1, k), new Vector2D(-1, 0)));
        c.Add(new CubicSegment(new Vector2D(-1, 0), new Vector2D(-1, -k), new Vector2D(-k, -1), new Vector2D(0, -1)));
        c.Add(new CubicSegment(new Vector2D(0, -1), new Vector2D(k, -1), new Vector2D(1, -k), new Vector2D(1, 0)));
        var s = new Shape();
        s.Contours.Add(c);
        return s;
    }

    public static TheoryData<string> Shapes => new() { "quad", "cubic" };

    private static Shape Make(string kind) => kind == "quad" ? QuadCircle() : CubicCircle();

    [Theory]
    [MemberData(nameof(Shapes))]
    public void RayThroughExtremumJoint_CountsOnce(string kind)
    {
        var s = Make(kind);
        // y = 0 passes exactly through the W and E joints (vertical-tangent x-extrema).
        s.WindingAt(new Vector2D(0, 0)).ShouldNotBe(0, "centre must be inside");
        s.WindingAt(new Vector2D(-2, 0)).ShouldBe(0, "point left of the shape must be outside (ray crosses both joints)");
        s.WindingAt(new Vector2D(2, 0)).ShouldBe(0, "point right of the shape must be outside");
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public void RayThroughTangentTouchJoint_NetsZero(string kind)
    {
        var s = Make(kind);
        // y = 1 grazes the N joint (horizontal tangent) — a touch, not a crossing.
        s.WindingAt(new Vector2D(-2, 1)).ShouldBe(0);
        s.WindingAt(new Vector2D(2, 1)).ShouldBe(0);
        // y = -1 grazes the S joint likewise.
        s.WindingAt(new Vector2D(-2, -1)).ShouldBe(0);
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public void OffJointRays_Sane(string kind)
    {
        var s = Make(kind);
        s.WindingAt(new Vector2D(0, 0.5)).ShouldNotBe(0);
        s.WindingAt(new Vector2D(0, -0.5)).ShouldNotBe(0);
        s.WindingAt(new Vector2D(-2, 0.5)).ShouldBe(0);
        s.WindingAt(new Vector2D(1.5, -0.5)).ShouldBe(0);
    }

    /// <summary>
    /// Render-level property over the fixture corpus: an MTSDF cell's border texels lie in the
    /// spread padding, which is always strictly outside the glyph — both the true-distance
    /// channel and the median must say so. The winding double-count violated exactly this (an
    /// inverted scanline ran inside-values out to the bitmap's edge).
    /// </summary>
    [Theory]
    [InlineData(Fixtures.DejaVuSans, 32f)]
    [InlineData(Fixtures.DejaVuSans, 48f)]
    [InlineData(Fixtures.DejaVuSans, 64f)]
    [InlineData(Fixtures.SourceSans3, 64f)]
    [InlineData(Fixtures.NotoSansSC, 48f)]
    public void Mtsdf_BorderTexels_AreOutside(string fixture, float ppem)
    {
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(fixture));
        foreach (var ch in "ocegbOCGRB08casdempq")
        {
            var gid = font.GetGlyphId(ch);
            if (gid == 0) continue;
            var m = font.RenderMtsdf(gid, ppem);
            if (m.IsEmpty) continue;

            for (var x = 0; x < m.Width; x++)
            {
                CheckOutside(m, x, 0, ch);
                CheckOutside(m, x, m.Height - 1, ch);
            }
            for (var y = 0; y < m.Height; y++)
            {
                CheckOutside(m, 0, y, ch);
                CheckOutside(m, m.Width - 1, y, ch);
            }
        }

        static void CheckOutside(MtsdfBitmap m, int x, int y, char ch)
        {
            var o = (y * m.Width + x) * 4;
            var median = Math.Max(Math.Min(m.Rgba[o], m.Rgba[o + 1]), Math.Min(Math.Max(m.Rgba[o], m.Rgba[o + 1]), m.Rgba[o + 2]));
            (m.Rgba[o + 3] <= 127).ShouldBeTrue($"'{ch}' border texel ({x},{y}) true-distance claims inside");
            (median <= 127).ShouldBeTrue($"'{ch}' border texel ({x},{y}) median claims inside");
        }
    }
}

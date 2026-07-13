using SharpAstro.Fonts.Rasterizer;

namespace SharpAstro.Fonts.Tests;

public class MsdfRasterizerTests
{
    private static readonly string DumpDir =
        System.IO.Path.Combine(AppContext.BaseDirectory, "BmpDumps");

    static MsdfRasterizerTests() => Directory.CreateDirectory(DumpDir);

    private static float Median(byte a, byte b, byte c)
    {
        var lo = Math.Max(Math.Min(a, b), Math.Min(Math.Max(a, b), c));
        return lo / 255f;
    }

    [Fact]
    public void EmptyOutline_ReturnsEmptyBitmap()
    {
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.DejaVuSans));
        var mtsdf = font.RenderMtsdf(font.GetGlyphId(' '), pixelsPerEm: 32f);
        mtsdf.IsEmpty.ShouldBeTrue();
    }

    [Theory]
    [InlineData('A')]
    [InlineData('B')]
    [InlineData('g')]
    [InlineData('Q')]
    [InlineData('e')]
    public void RenderMtsdf_ProducesSensibleField(int codepoint)
    {
        // 64 px so stems are several pixels wide: interior texels then encode a distance well clear of the
        // 0.5 edge (value = 0.5 + dist/(2*spread)), which a thin 32 px stem can't reach.
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.DejaVuSans));
        const float spread = 4f;
        var mtsdf = font.RenderMtsdf(font.GetGlyphId((uint)codepoint), 64f, spread);

        mtsdf.IsEmpty.ShouldBeFalse();
        mtsdf.Width.ShouldBeInRange(8, 160);
        mtsdf.Height.ShouldBeInRange(8, 160);
        mtsdf.Rgba.Length.ShouldBe(mtsdf.Width * mtsdf.Height * 4);
        mtsdf.Spread.ShouldBe(spread);

        var interiorMedianOverHalf = false;   // some texel reconstructs as clearly inside
        var interiorAlphaOverHalf = false;
        var exteriorMedianUnderHalf = false;   // some texel clearly outside
        var edgeBand = false;                  // some texel sits on the outline
        var signMismatches = 0;

        for (var i = 0; i < mtsdf.Width * mtsdf.Height; i++)
        {
            var o = i * 4;
            var median = Median(mtsdf.Rgba[o], mtsdf.Rgba[o + 1], mtsdf.Rgba[o + 2]);
            var alpha = mtsdf.Rgba[o + 3] / 255f;

            if (median > 0.65f) interiorMedianOverHalf = true;
            if (alpha > 0.65f) interiorAlphaOverHalf = true;
            if (median < 0.35f) exteriorMedianUnderHalf = true;
            if (Math.Abs(median - 0.5f) < 0.03f) edgeBand = true;

            // median and the true-distance channel must agree on inside/outside almost everywhere;
            // they may legitimately diverge in a thin band right at the outline (a single channel dips
            // strongly negative at a corner while the median stays inside — the point of MTSDF).
            if (median > 0.5f != alpha > 0.5f && Math.Abs(median - 0.5f) > 0.1f)
                signMismatches++;
        }

        interiorMedianOverHalf.ShouldBeTrue("no clearly-inside texel via median(r,g,b)");
        interiorAlphaOverHalf.ShouldBeTrue("no clearly-inside texel via the true-distance (alpha) channel");
        exteriorMedianUnderHalf.ShouldBeTrue("no clearly-outside texel (padding should be outside)");
        edgeBand.ShouldBeTrue("no texel lands on the reconstructed outline");

        // Away from the immediate outline, median(RGB) and alpha should classify inside/outside identically.
        var mismatchFraction = (float)signMismatches / (mtsdf.Width * mtsdf.Height);
        mismatchFraction.ShouldBeLessThan(0.02f);
    }

    [Fact]
    public void Mtsdf_AlphaChannel_TracksSingleChannelSdf()
    {
        // The MTSDF alpha channel is the plain true signed distance with the same ±spread → [0,1] encoding
        // as SdfRasterizer, so it should classify inside/outside like the single-channel field. Compare the
        // inside-area fraction (robust to a one-pixel grid difference between the two rasterizers).
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.DejaVuSans));
        const float ppem = 48f;
        const float spread = 4f;
        var gid = font.GetGlyphId('B');

        var sdf = font.RenderSdf(gid, ppem, spread);
        var mtsdf = font.RenderMtsdf(gid, ppem, spread);

        sdf.IsEmpty.ShouldBeFalse();
        mtsdf.IsEmpty.ShouldBeFalse();

        var sdfInside = sdf.Alpha.Count(a => a > 128) / (float)sdf.Alpha.Length;
        var mtsdfInsideCount = 0;
        for (var i = 0; i < mtsdf.Width * mtsdf.Height; i++)
            if (mtsdf.Rgba[i * 4 + 3] > 128) mtsdfInsideCount++;
        var mtsdfInside = mtsdfInsideCount / (float)(mtsdf.Width * mtsdf.Height);

        // Both should mark a substantial-but-not-dominant interior, and agree closely on how much.
        sdfInside.ShouldBeInRange(0.15f, 0.85f);
        mtsdfInside.ShouldBeInRange(0.15f, 0.85f);
        Math.Abs(mtsdfInside - sdfInside).ShouldBeLessThan(0.04f);
    }

    [Fact]
    public void RenderMtsdf_Cff_ExercisesCubicPath()
    {
        // SourceSans3 is a CFF/OTF font — its outlines are cubic Béziers, exercising CubicSegment.
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.SourceSans3));
        var mtsdf = font.RenderMtsdf(font.GetGlyphId('a'), 48f);

        mtsdf.IsEmpty.ShouldBeFalse();
        mtsdf.Rgba.Length.ShouldBe(mtsdf.Width * mtsdf.Height * 4);

        var hasInside = false;
        var hasOutside = false;
        for (var i = 0; i < mtsdf.Width * mtsdf.Height; i++)
        {
            var o = i * 4;
            var median = Median(mtsdf.Rgba[o], mtsdf.Rgba[o + 1], mtsdf.Rgba[o + 2]);
            if (median > 0.7f) hasInside = true;
            if (median < 0.3f) hasOutside = true;
        }

        hasInside.ShouldBeTrue();
        hasOutside.ShouldBeTrue();
    }

    [Theory]
    [InlineData(Fixtures.DejaVuSans, 'R', 64f)]
    [InlineData(Fixtures.DejaVuSans, 'B', 64f)]
    [InlineData(Fixtures.DejaVuSans, 'M', 64f)]
    [InlineData(Fixtures.DejaVuSans, 'W', 64f)]
    [InlineData(Fixtures.DejaVuSans, 'g', 64f)]
    [InlineData(Fixtures.DejaVuSans, '8', 64f)]
    [InlineData(Fixtures.SourceSans3, 'R', 64f)]
    [InlineData(Fixtures.SourceSans3, 'O', 64f)]
    [InlineData(Fixtures.NotoSansSC, '国', 32f)]
    [InlineData(Fixtures.NotoSansSC, '龍', 32f)]
    public void RenderMtsdf_InterpolatedMedian_HasNoSpuriousExtrema(string fixture, int codepoint, float ppem)
    {
        // GPU text rendering bilinearly interpolates the R,G,B channels and THEN takes the median.
        // Along the segment between two texels the median is piecewise-linear (it can only turn where
        // two channels cross), so it can develop a spurious extremum that overshoots past both
        // endpoint medians — reconstructing a phantom edge where the glyph has no outline at all
        // (seen in the wild as a stray bar bridging a bold 'R''s baseline legs). The generator's
        // interpolation error-correction pass must leave no such extremum: for every adjacent texel
        // pair (including diagonals), the interpolated median stays within the interval spanned by
        // the endpoint medians, up to the pass's epsilon plus byte-quantization slack.
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(fixture));
        var gid = font.GetGlyphId((uint)codepoint);
        gid.ShouldNotBe(0u);
        var m = font.RenderMtsdf(gid, ppem);
        m.IsEmpty.ShouldBeFalse();

        const float eps = 0.03f; // pass epsilon 0.02 + quantization slack
        (int dx, int dy)[] neighbors = [(1, 0), (0, 1), (1, 1), (-1, 1)];
        var worst = 0f;
        for (var y = 0; y < m.Height; y++)
        {
            for (var x = 0; x < m.Width; x++)
            {
                var o = (y * m.Width + x) * 4;
                float r0 = m.Rgba[o] / 255f, g0 = m.Rgba[o + 1] / 255f, b0 = m.Rgba[o + 2] / 255f;
                var m0 = MedianF(r0, g0, b0);
                foreach (var (dx, dy) in neighbors)
                {
                    int nx = x + dx, ny = y + dy;
                    if ((uint)nx >= (uint)m.Width || (uint)ny >= (uint)m.Height) continue;
                    var p = (ny * m.Width + nx) * 4;
                    float r1 = m.Rgba[p] / 255f, g1 = m.Rgba[p + 1] / 255f, b1 = m.Rgba[p + 2] / 255f;
                    var m1 = MedianF(r1, g1, b1);
                    var lo = Math.Min(m0, m1);
                    var hi = Math.Max(m0, m1);
                    // the median can only turn where two channels cross — check those points
                    foreach (var t in new[] { CrossT(r0, r1, g0, g1), CrossT(g0, g1, b0, b1), CrossT(r0, r1, b0, b1) })
                    {
                        if (float.IsNaN(t) || t <= 0f || t >= 1f) continue;
                        var mt = MedianF(Lerp(r0, r1, t), Lerp(g0, g1, t), Lerp(b0, b1, t));
                        worst = Math.Max(worst, Math.Max(lo - mt, mt - hi));
                    }
                }
            }
        }
        worst.ShouldBeLessThan(eps,
            "interpolated median overshoots its endpoint interval — phantom-edge artifact survived error correction");

        static float MedianF(float a, float b, float c) => Math.Max(Math.Min(a, b), Math.Min(Math.Max(a, b), c));
        static float Lerp(float a, float b, float t) => a + (b - a) * t;
        static float CrossT(float a0, float a1, float b0, float b1)
        {
            var d = (a1 - a0) - (b1 - b0);
            return Math.Abs(d) < 1e-6f ? float.NaN : (b0 - a0) / d;
        }
    }

    [Fact]
    public void DumpChannels()
    {
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.DejaVuSans));
        foreach (var ch in "AaBgQ")
        {
            var gid = font.GetGlyphId(ch);
            if (gid == 0) continue;
            var mtsdf = font.RenderMtsdf(gid, 64f);
            if (mtsdf.IsEmpty) continue;

            var n = mtsdf.Width * mtsdf.Height;
            var alpha = new byte[n];
            var median = new byte[n];
            for (var i = 0; i < n; i++)
            {
                alpha[i] = mtsdf.Rgba[i * 4 + 3];
                var lo = Math.Max(
                    Math.Min(mtsdf.Rgba[i * 4], mtsdf.Rgba[i * 4 + 1]),
                    Math.Min(Math.Max(mtsdf.Rgba[i * 4], mtsdf.Rgba[i * 4 + 1]), mtsdf.Rgba[i * 4 + 2]));
                median[i] = (byte)lo;
            }

            BmpWriter.WriteGray8(System.IO.Path.Combine(DumpDir, $"mtsdf_{ch}_alpha.bmp"), alpha, mtsdf.Width, mtsdf.Height);
            BmpWriter.WriteGray8(System.IO.Path.Combine(DumpDir, $"mtsdf_{ch}_median.bmp"), median, mtsdf.Width, mtsdf.Height);
        }
    }
}

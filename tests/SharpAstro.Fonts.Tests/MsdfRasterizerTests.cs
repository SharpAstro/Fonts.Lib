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

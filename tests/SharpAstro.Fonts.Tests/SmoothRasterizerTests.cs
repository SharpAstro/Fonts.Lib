using SharpAstro.Fonts.Rasterizer;

namespace SharpAstro.Fonts.Tests;

public class SmoothRasterizerTests
{
    private static readonly string DumpDir =
        System.IO.Path.Combine(AppContext.BaseDirectory, "BmpDumps");

    static SmoothRasterizerTests() => Directory.CreateDirectory(DumpDir);

    [Fact]
    public void EmptyOutline_ReturnsEmptyBitmap()
    {
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.DejaVuSans));
        var bmp = font.RenderGlyph(font.GetGlyphId(' '), pixelsPerEm: 32f);
        bmp.IsEmpty.ShouldBeTrue();
    }

    [Theory]
    [InlineData('A')]
    [InlineData('B')]
    [InlineData('g')]
    [InlineData('Q')]
    public void RenderGlyph_ProducesSensibleBitmap(int codepoint)
    {
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.DejaVuSans));
        var bmp = font.RenderGlyph(font.GetGlyphId((uint)codepoint), 32f);

        bmp.IsEmpty.ShouldBeFalse();
        bmp.Width.ShouldBeInRange(8, 64);
        bmp.Height.ShouldBeInRange(8, 64);
        bmp.Alpha.Length.ShouldBe(bmp.Width * bmp.Height);

        // At least one fully-opaque pixel — confirms interior fill works.
        bmp.Alpha.Max().ShouldBe((byte)255);
        // At least one transparent pixel — confirms we're not flooding.
        bmp.Alpha.Min().ShouldBe((byte)0);
        // Anti-aliasing: must produce intermediate gray values at edges.
        bmp.Alpha.Any(a => a > 0 && a < 255).ShouldBeTrue();
    }

    [Fact]
    public void DumpAtMultipleSizes()
    {
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.DejaVuSans));
        foreach (var ch in "AaBbQg.0987éü")
        foreach (var size in new[] { 12f, 24f, 48f, 96f })
        {
            var gid = font.GetGlyphId(ch);
            if (gid == 0) continue;
            var bmp = font.RenderGlyph(gid, size);
            if (bmp.IsEmpty) continue;
            var name = $"DejaVu_{(int)size:D3}px_U+{(int)ch:X4}_{ch}.bmp";
            BmpWriter.WriteGray8(System.IO.Path.Combine(DumpDir, name),
                bmp.Alpha, bmp.Width, bmp.Height);
        }
    }

    [Fact]
    public void Rasterizer_IsConcurrentlyCallable()
    {
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.DejaVuSans));
        var gid = font.GetGlyphId('M');
        var expected = font.RenderGlyph(gid, 24f);

        Parallel.For(0, 256, _ =>
        {
            var bmp = font.RenderGlyph(gid, 24f);
            bmp.Width.ShouldBe(expected.Width);
            bmp.Height.ShouldBe(expected.Height);
            bmp.Alpha.AsSpan().SequenceEqual(expected.Alpha.AsSpan()).ShouldBeTrue();
        });
    }

    [Fact]
    public void BitmapMetrics_LookSane()
    {
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.DejaVuSans));

        var capH = font.RenderGlyph(font.GetGlyphId('H'), 32f);
        var lower = font.RenderGlyph(font.GetGlyphId('x'), 32f);
        var descender = font.RenderGlyph(font.GetGlyphId('g'), 32f);

        // Top should be positive for letters that extend above baseline.
        capH.Top.ShouldBeGreaterThan(0);
        lower.Top.ShouldBeGreaterThan(0);
        // Capital H should be taller than lowercase x.
        capH.Height.ShouldBeGreaterThan(lower.Height);
        // 'g' has a descender, so its bitmap height should exceed the bitmap
        // top metric (i.e. some of the bitmap is below the baseline).
        descender.Height.ShouldBeGreaterThan(descender.Top);
    }
}

using SharpAstro.Fonts.Outlines;
using SharpAstro.Fonts.Type1;

namespace SharpAstro.Fonts.Tests;

public class Type1Tests
{
    private static readonly string DumpDir =
        System.IO.Path.Combine(AppContext.BaseDirectory, "BmpDumps");

    static Type1Tests() => Directory.CreateDirectory(DumpDir);

    private const string CmrFile = "cmr10.pfb";

    [Fact]
    public void Cmr10_LoadsAndHasGlyphNames()
    {
        var font = Type1Font.LoadPfbFromFile(Fixtures.Path(CmrFile));
        font.GlyphNames.Count.ShouldBeGreaterThan(0);
        // Computer Modern Roman uses TeX-style encoding; common letters are present.
        font.HasGlyph("A").ShouldBeTrue();
        font.HasGlyph("B").ShouldBeTrue();
        font.HasGlyph(".notdef").ShouldBeTrue();
    }

    [Fact]
    public void Cmr10_FontMatrix_IsTypicalType1()
    {
        var font = Type1Font.LoadPfbFromFile(Fixtures.Path(CmrFile));
        // Standard Type 1: [0.001 0 0 0.001 0 0]
        font.FontMatrix[0].ShouldBe(0.001f, tolerance: 0.0001f);
        font.FontMatrix[3].ShouldBe(0.001f, tolerance: 0.0001f);
        font.UnitsPerEm.ShouldBe(1000);
    }

    [Fact]
    public void Cmr10_DrawGlyph_EmitsCommands()
    {
        var font = Type1Font.LoadPfbFromFile(Fixtures.Path(CmrFile));
        var sink = new CountingSink();
        var ok = font.DrawGlyph("A", sink);
        ok.ShouldBeTrue();
        sink.MoveCount.ShouldBeGreaterThan(0);
        // Type 1 uses cubic Béziers; well-formed letters should have at least one curve.
        (sink.CubicCount + sink.LineCount).ShouldBeGreaterThan(0);
        sink.CloseCount.ShouldBeGreaterThan(0);
    }

    [Theory]
    [InlineData("A")]
    [InlineData("B")]
    [InlineData("g")]
    [InlineData("Q")]
    public void Cmr10_RenderGlyph_ProducesAaBitmap(string name)
    {
        var font = Type1Font.LoadPfbFromFile(Fixtures.Path(CmrFile));
        var bmp = font.RenderGlyph(name, 64f);
        bmp.IsEmpty.ShouldBeFalse();
        bmp.Alpha.Max().ShouldBe((byte)255);
        bmp.Alpha.Min().ShouldBe((byte)0);
        bmp.Alpha.Any(a => a > 0 && a < 255).ShouldBeTrue();
    }

    [Fact]
    public void Cmr10_DumpAaBmps()
    {
        var font = Type1Font.LoadPfbFromFile(Fixtures.Path(CmrFile));
        foreach (var name in new[] { "A", "B", "g", "Q", "comma", "period" })
        {
            if (!font.HasGlyph(name)) continue;
            var bmp = font.RenderGlyph(name, 64f);
            if (bmp.IsEmpty) continue;
            BmpWriter.WriteGray8(System.IO.Path.Combine(DumpDir, $"cmr10_{name}_64px.bmp"),
                bmp.Alpha, bmp.Width, bmp.Height);
        }
    }

    [Fact]
    public void Cmr10_IsConcurrentlyRenderable()
    {
        var font = Type1Font.LoadPfbFromFile(Fixtures.Path(CmrFile));
        var expected = font.RenderGlyph("A", 32f);
        Parallel.For(0, 64, _ =>
        {
            var bmp = font.RenderGlyph("A", 32f);
            bmp.Width.ShouldBe(expected.Width);
            bmp.Height.ShouldBe(expected.Height);
        });
    }

    private sealed class CountingSink : IGlyphSink
    {
        public int MoveCount, LineCount, QuadCount, CubicCount, CloseCount;
        public void MoveTo(float x, float y) => MoveCount++;
        public void LineTo(float x, float y) => LineCount++;
        public void QuadTo(float cx, float cy, float x, float y) => QuadCount++;
        public void CubicTo(float c1x, float c1y, float c2x, float c2y, float x, float y) => CubicCount++;
        public void Close() => CloseCount++;
    }
}

using SharpAstro.Fonts.Outlines;

namespace SharpAstro.Fonts.Tests;

public class CffTests
{
    [Fact]
    public void SourceSans_IsCff()
    {
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.SourceSans3));
        font.Directory.IsCff.ShouldBeTrue();
        font.Directory.IsTrueType.ShouldBeFalse();
        font.HasCffOutlines.ShouldBeTrue();
        font.Glyf.ShouldBeNull();
    }

    [Fact]
    public void SourceSans_HasReasonableGlyphCount()
    {
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.SourceSans3));
        // Source Sans 3 Regular has ~2k glyphs.
        font.NumGlyphs.ShouldBeGreaterThan((ushort)1000);
    }

    [Theory]
    [InlineData('A')]
    [InlineData('z')]
    [InlineData('0')]
    public void SourceSans_BasicAscii_MapsToGlyph(int codepoint)
    {
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.SourceSans3));
        font.GetGlyphId((uint)codepoint).ShouldBeGreaterThan(0u);
    }

    [Fact]
    public void SourceSans_LoadGlyphOutline_ThrowsForCff()
    {
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.SourceSans3));
        var gid = font.GetGlyphId('A');
        Should.Throw<NotSupportedException>(() => font.LoadGlyphOutline(gid));
    }

    [Fact]
    public void SourceSans_DrawGlyph_EmitsCommands()
    {
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.SourceSans3));
        var sink = new CountingSink();
        font.DrawGlyph(font.GetGlyphId('A'), sink);

        sink.MoveCount.ShouldBeGreaterThan(0);
        sink.CloseCount.ShouldBe(sink.MoveCount);
        // CFF emits cubics, not quads, for non-trivial letters.
        sink.CubicCount.ShouldBeGreaterThan(0);
    }

    [Theory]
    [InlineData('A')]
    [InlineData('B')]
    [InlineData('Q')]
    [InlineData('g')]
    public void SourceSans_RenderGlyph_ProducesAaBitmap(int codepoint)
    {
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.SourceSans3));
        var bmp = font.RenderGlyph(font.GetGlyphId((uint)codepoint), 32f);

        bmp.IsEmpty.ShouldBeFalse();
        bmp.Alpha.Max().ShouldBe((byte)255);
        bmp.Alpha.Min().ShouldBe((byte)0);
        bmp.Alpha.Any(a => a > 0 && a < 255).ShouldBeTrue();
    }

    [Fact]
    public void SourceSans_AllGlyphs_RenderWithoutThrowing()
    {
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.SourceSans3));
        var sink = new CountingSink();
        for (uint gid = 0; gid < font.NumGlyphs; gid++)
        {
            sink.Reset();
            font.DrawGlyph(gid, sink);
        }
    }

    [Fact]
    public void SourceSans_DumpAa_BMPs()
    {
        var dumpDir = System.IO.Path.Combine(AppContext.BaseDirectory, "BmpDumps");
        Directory.CreateDirectory(dumpDir);
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.SourceSans3));
        foreach (var ch in "AaBbQg.0987éü")
        foreach (var size in new[] { 12f, 24f, 48f, 96f })
        {
            var gid = font.GetGlyphId(ch);
            if (gid == 0) continue;
            var bmp = font.RenderGlyph(gid, size);
            if (bmp.IsEmpty) continue;
            var name = $"SourceSans_{(int)size:D3}px_U+{(int)ch:X4}_{ch}.bmp";
            BmpWriter.WriteGray8(System.IO.Path.Combine(dumpDir, name),
                bmp.Alpha, bmp.Width, bmp.Height);
        }
    }

    private sealed class CountingSink : IGlyphSink
    {
        public int MoveCount, LineCount, QuadCount, CubicCount, CloseCount;
        public void Reset() { MoveCount = LineCount = QuadCount = CubicCount = CloseCount = 0; }
        public void MoveTo(float x, float y) => MoveCount++;
        public void LineTo(float x, float y) => LineCount++;
        public void QuadTo(float cx, float cy, float x, float y) => QuadCount++;
        public void CubicTo(float c1x, float c1y, float c2x, float c2y, float x, float y) => CubicCount++;
        public void Close() => CloseCount++;
    }
}

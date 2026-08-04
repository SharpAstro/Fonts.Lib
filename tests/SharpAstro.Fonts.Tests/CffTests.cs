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

    // ---- Glyph-name → GID (PDF simple-font /Encoding route) --------------------------

    [Theory]
    [InlineData("one", 15u)]   // the Canon manual's step numbers: code 0x31 via WinAnsi
    [InlineData("two", 16u)]
    [InlineData("three", 17u)]
    [InlineData("W", 49u)]     // what charCode-as-GID used to (wrongly) select for 0x31
    public void LithosBold_BareCff_ResolvesGlyphNames(string name, uint expectedGid)
    {
        // Bare name-keyed CFF with no Encoding operator: the charset is the only name
        // authority, so GetGlyphIdByName must work for glyph selection to be possible.
        var font = OpenTypeFont.Load(File.ReadAllBytes(Fixtures.Path(Fixtures.LithosBold_Subset)));
        font.GetGlyphIdByName(name).ShouldBe(expectedGid);
    }

    [Fact]
    public void LithosBold_UnknownName_ReturnsZero()
    {
        var font = OpenTypeFont.Load(File.ReadAllBytes(Fixtures.Path(Fixtures.LithosBold_Subset)));
        font.GetGlyphIdByName("uni4E00").ShouldBe(0u);
    }

    [Theory]
    [InlineData("one", '1')]
    [InlineData("A", 'A')]
    [InlineData("germandbls", 'ß')]
    public void SourceSans_NameLookup_AgreesWithCmap(string name, char ch)
    {
        // Full (non-subset) name-keyed OTF: the name route and the Unicode cmap route
        // must land on the same glyph.
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.SourceSans3));
        var byName = font.GetGlyphIdByName(name);
        byName.ShouldBeGreaterThan(0u);
        byName.ShouldBe(font.GetGlyphId(ch));
    }

    [Fact]
    public void CidKeyedCff_NameLookup_ReturnsZero()
    {
        // CID charsets hold CIDs, not name SIDs — the name route must refuse, not
        // fabricate a gid from a CID that happens to collide.
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.NotoSansJP));
        font.GetGlyphIdByName("one").ShouldBe(0u);
    }

    [Fact]
    public void StandardStrings_TableIsComplete()
    {
        Tables.Cff.CffStandardStrings.Count.ShouldBe(391);
        Tables.Cff.CffStandardStrings.Get(0).ShouldBe(".notdef");
        Tables.Cff.CffStandardStrings.Get(18).ShouldBe("one");
        Tables.Cff.CffStandardStrings.Get(56).ShouldBe("W");
        Tables.Cff.CffStandardStrings.Get(96).ShouldBe("exclamdown");
        Tables.Cff.CffStandardStrings.Get(104).ShouldBe("quotesingle");
        Tables.Cff.CffStandardStrings.Get(390).ShouldBe("Semibold");
        Tables.Cff.CffStandardStrings.IndexOf("germandbls").ShouldBe(149);
        Tables.Cff.CffStandardStrings.IndexOf("nosuchglyph").ShouldBe(-1);
    }
}

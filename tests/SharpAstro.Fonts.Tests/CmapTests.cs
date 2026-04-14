namespace SharpAstro.Fonts.Tests;

public class CmapTests
{
    [Fact]
    public void DejaVuSans_HasUnicodeSubtable()
    {
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.DejaVuSans));
        font.Cmap.Subtables.Count.ShouldBeGreaterThan(0);
        font.Cmap.PreferredUnicodeSubtable().ShouldNotBeNull();
    }

    [Theory]
    [InlineData('A')]
    [InlineData('z')]
    [InlineData('0')]
    [InlineData(' ')]
    public void DejaVuSans_BasicAscii_MapsToNonZeroGlyphId(int codepoint)
    {
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.DejaVuSans));
        var gid = font.GetGlyphId((uint)codepoint);
        gid.ShouldBeGreaterThan(0u);
        gid.ShouldBeLessThan(font.NumGlyphs);
    }

    [Fact]
    public void DejaVuSans_UnmappedCodepoint_ReturnsZero()
    {
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.DejaVuSans));
        // U+10FFFD is a private-use codepoint that DejaVu Sans does not map.
        font.GetGlyphId(0x10FFFDu).ShouldBe(0u);
    }

    [Theory]
    [InlineData(Fixtures.XXTIIT_Arial_Subset)]
    [InlineData(Fixtures.Tahoma_Subset)]
    [InlineData(Fixtures.ISOCPEUR_Subset)]
    public void SubsetFonts_HaveAtLeastOneSubtable(string fontFile)
    {
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(fontFile));
        font.Cmap.Subtables.Count.ShouldBeGreaterThan(0);
    }

    [Theory]
    [InlineData(Fixtures.NotoSansJP)]
    [InlineData(Fixtures.NotoSansKR)]
    [InlineData(Fixtures.NotoSansSC)]
    [InlineData(Fixtures.NotoSansTC)]
    public void CJK_HasFormat14Subtable(string fontFile)
    {
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(fontFile));
        // Format 14 is always platform=0 encoding=5.
        var f14 = font.Cmap.Find(0, 5);
        f14.ShouldNotBeNull();
        f14.Format.ShouldBe((ushort)14);
    }

    [Fact]
    public void NotoSansJP_NonDefaultIVS_ReturnsDifferentGlyph()
    {
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.NotoSansJP));
        // U+4FAE (侮) base GID = 2684; with U+FE00 → GID 15189 (non-default mapping).
        var baseGid = font.GetGlyphId(0x4FAE);
        baseGid.ShouldBe(2684u);

        var varGid = font.GetGlyphId(0x4FAE, 0xFE00);
        varGid.ShouldBe(15189u);
        varGid.ShouldNotBe(baseGid);
    }

    [Fact]
    public void NotoSansJP_DefaultIVS_ReturnsSameAsBase()
    {
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.NotoSansJP));
        // U+E0100 has 4693 default UVS ranges in NotoSansJP — a codepoint that
        // falls in a default range should return the same GID as the base lookup.
        // U+4E00 (一) is a basic CJK codepoint likely in the default range.
        var baseGid = font.GetGlyphId(0x4E00);
        baseGid.ShouldBeGreaterThan(0u);

        var varGid = font.GetGlyphId(0x4E00, 0xE0100);
        varGid.ShouldBe(baseGid);
    }

    [Fact]
    public void NotoSansJP_IVS_E0101_NonDefault()
    {
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.NotoSansJP));
        // U+3402 base GID = 2185; with U+E0101 → GID 16375 (non-default mapping).
        var baseGid = font.GetGlyphId(0x3402);
        baseGid.ShouldBe(2185u);

        var varGid = font.GetGlyphId(0x3402, 0xE0101);
        varGid.ShouldBe(16375u);
        varGid.ShouldNotBe(baseGid);
    }

    [Fact]
    public void NotoSansJP_UnmappedVariationSelector_ReturnsZero()
    {
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.NotoSansJP));
        // A variation selector that has no record at all should return 0.
        font.GetGlyphId(0x4FAE, 0xE01EF).ShouldBe(0u);
    }

    [Fact]
    public void NotoSansJP_BaseCJK_MapsWithoutVariationSelector()
    {
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.NotoSansJP));
        // Basic CJK codepoints should map normally without a variation selector.
        font.GetGlyphId(0x4E00).ShouldBeGreaterThan(0u); // 一
        font.GetGlyphId(0x6F22).ShouldBeGreaterThan(0u); // 漢
    }
}

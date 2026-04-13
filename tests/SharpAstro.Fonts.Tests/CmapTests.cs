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
}

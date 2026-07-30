namespace SharpAstro.Fonts.Tests;

/// <summary>
/// 'name' / 'OS/2' parsing — the tables that let a caller ask a font what it is called,
/// rather than inferring it from the file name.
/// </summary>
public class NameTableTests
{
    [Theory]
    [InlineData(Fixtures.DejaVuSans, "DejaVu Sans", "DejaVuSans")]
    [InlineData(Fixtures.SourceSans3, "Source Sans 3", "SourceSans3-Regular")]
    [InlineData(Fixtures.NotoSansJP, "Noto Sans JP", "NotoSansJP-Regular")]
    [InlineData(Fixtures.RobotoFlex, "Roboto Flex", "RobotoFlex-Regular")]
    [InlineData(Fixtures.Merida, "Chess Merida Unicode", "ChessMeridaUnicode")]
    public void Name_ReportsFamilyAndPostScriptName(string fixture, string family, string postScript)
    {
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(fixture));
        var name = font.Name.ShouldNotBeNull();
        name.Family.ShouldBe(family);
        name.PostScriptName.ShouldBe(postScript);
    }

    /// <summary>
    /// DejaVu's subfamily is "Book", not "Regular" — a reminder that the subfamily string is
    /// the font's own vocabulary and can't be pattern-matched as if it were an enum.
    /// </summary>
    [Fact]
    public void Name_SubfamilyIsWhateverTheFontSays()
    {
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.DejaVuSans));
        font.Name!.Subfamily.ShouldBe("Book");
    }

    /// <summary>
    /// PDF subset fonts routinely ship a 'name' table carrying only the PostScript name —
    /// no family, no subfamily. That must be a null, not a throw or an invented string.
    /// </summary>
    [Theory]
    [InlineData(Fixtures.Tahoma_Subset, "Tahoma")]
    [InlineData(Fixtures.XXTIIT_Arial_Subset, "Arial")]
    [InlineData(Fixtures.ISOCPEUR_Subset, "ISOCPEUR")]
    public void Name_SubsetFontWithOnlyPostScriptName(string fixture, string postScript)
    {
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(fixture));
        var name = font.Name.ShouldNotBeNull();
        name.PostScriptName.ShouldBe(postScript);
        name.Family.ShouldBeNull();
        name.Subfamily.ShouldBeNull();
    }

    [Fact]
    public void Os2_ReportsWeightAndStyle()
    {
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.SourceSans3));
        var os2 = font.Os2.ShouldNotBeNull();
        os2.WeightClass.ShouldBe((ushort)400);
        os2.IsBold.ShouldBeFalse();
        os2.IsItalic.ShouldBeFalse();
        os2.Panose.Length.ShouldBe(10);
    }

    /// <summary>
    /// A PDF subset with a (3,0) cmap and no Unicode subtable is symbol-encoded: its char codes
    /// reach glyphs through the F000 private-use block, not through Unicode. This is the
    /// distinction the PDF font descriptor's /Symbolic flag draws.
    /// </summary>
    [Theory]
    [InlineData(Fixtures.Tahoma_Subset)]
    [InlineData(Fixtures.XXTIIT_Arial_Subset)]
    [InlineData(Fixtures.ISOCPEUR_Subset)]
    public void IsSymbolEncoded_TrueForSymbolCmapOnlyFonts(string fixture)
    {
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(fixture));
        font.IsSymbolEncoded.ShouldBeTrue();
    }

    /// <summary>
    /// The counterpart that matters more: a font full of symbols is NOT symbol-encoded if it
    /// maps them at their real Unicode codepoints. Nothing about "contains arrows and ballot
    /// boxes" is visible in font metadata — picking a face to draw such a glyph is a coverage
    /// question, which is why this property must not be used for that.
    /// </summary>
    [Theory]
    [InlineData(Fixtures.DejaVuSans)]
    [InlineData(Fixtures.NotoColorEmoji)]
    [InlineData(Fixtures.BabelStoneXiangqiColour)]
    [InlineData(Fixtures.Merida)]
    public void IsSymbolEncoded_FalseForUnicodeMappedFonts(string fixture)
    {
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(fixture));
        font.IsSymbolEncoded.ShouldBeFalse();
    }

    /// <summary>Repeated access is memoized and stable (the tables are parsed lazily).</summary>
    [Fact]
    public void NameAndOs2_AreStableAcrossAccesses()
    {
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.DejaVuSans));
        font.Name.ShouldBeSameAs(font.Name);
        font.Os2.ShouldBeSameAs(font.Os2);
    }

    /// <summary>
    /// A bare CFF program (a PDF CIDFontType0 /FontFile3) has no SFNT wrapper and therefore no
    /// 'name'/'OS/2' at all. Callers get nulls rather than an exception, so a resolver can scan
    /// indiscriminately — and <see cref="OpenTypeFont.IsSymbolEncoded"/> stays answerable.
    /// </summary>
    [Fact]
    public void Name_NullWhenFontHasNoNameTable()
    {
        var wrapped = OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.SourceSans3));
        wrapped.TryGetTable(IO.Tag.Parse("CFF "), out var cff).ShouldBeTrue();

        var bare = OpenTypeFont.Load(cff.ToArray());
        bare.Name.ShouldBeNull();
        bare.Os2.ShouldBeNull();
        bare.IsSymbolEncoded.ShouldBeFalse();
    }
}

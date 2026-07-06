using SharpAstro.Fonts.IO;
using SharpAstro.Fonts.Shaping.Otl;

namespace SharpAstro.Fonts.Shaping.Tests;

/// <summary>
/// H0 parse coverage for the OT-layout core: ScriptList/FeatureList/LookupList,
/// GDEF classes, and the raw-table seam (<see cref="OpenTypeFont.TryGetTable"/>).
/// DejaVuSans is the known-good structural fixture (GSUB liga + GPOS kern + GDEF
/// mark classes); every bundled fixture font must parse without throwing — a
/// malformed or exotic layout table degrades to "no shaping", never an exception.
/// </summary>
public class OtlParseTests
{
    private static readonly string FixtureDir = Path.Combine(AppContext.BaseDirectory, "Fixtures");
    private static string FontPath(string name) => Path.Combine(FixtureDir, name);

    private static ShapingFont LoadDejaVu()
        => ShapingFont.Create(OpenTypeFont.LoadFromFile(FontPath("DejaVuSans.ttf")));

    [Fact]
    public void TryGetTable_ReturnsRawTables_AndFalseForMissing()
    {
        var font = OpenTypeFont.LoadFromFile(FontPath("DejaVuSans.ttf"));

        font.TryGetTable(new Tag("GSUB"), out var gsub).ShouldBeTrue();
        gsub.Length.ShouldBeGreaterThan(10);
        // Layout tables start with version 1.x — first uint16 is the major version.
        gsub.Span[0].ShouldBe((byte)0);
        gsub.Span[1].ShouldBe((byte)1);

        font.TryGetTable(new Tag("Zzz9"), out _).ShouldBeFalse();
    }

    [Fact]
    public void DejaVu_ParsesGsubGposGdef_WithExpectedStructure()
    {
        var shaping = LoadDejaVu();

        shaping.HasSubstitution.ShouldBeTrue();
        shaping.HasPositioning.ShouldBeTrue();

        // latn is present in both tables' ScriptLists.
        shaping.Gsub!.ScriptTags.ShouldContain(new Tag("latn"));
        shaping.Gpos!.ScriptTags.ShouldContain(new Tag("latn"));

        // The classic feature inventory this whole track exists for.
        shaping.Gsub.FeatureTags.ShouldContain(new Tag("liga"));
        shaping.Gpos.FeatureTags.ShouldContain(new Tag("kern"));
        shaping.Gpos.FeatureTags.ShouldContain(new Tag("mark"));

        shaping.Gsub.Lookups.Length.ShouldBeGreaterThan(0);
        shaping.Gpos.Lookups.Length.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void DejaVu_Gdef_ClassifiesMarksAndBases()
    {
        var shaping = LoadDejaVu();
        var font = shaping.Font;

        var baseGid = font.GetGlyphId('A');
        var markGid = font.GetGlyphId(0x0301); // combining acute accent

        baseGid.ShouldNotBe(0u);
        markGid.ShouldNotBe(0u);
        shaping.Gdef.GetGlyphClass(baseGid).ShouldBe(GlyphClass.Base);
        shaping.Gdef.GetGlyphClass(markGid).ShouldBe(GlyphClass.Mark);
    }

    [Fact]
    public void EveryFixtureFont_ParsesWithoutThrowing()
    {
        var fonts = Directory.EnumerateFiles(FixtureDir, "*.ttf")
            .Concat(Directory.EnumerateFiles(FixtureDir, "*.otf"))
            .ToList();
        fonts.Count.ShouldBeGreaterThan(5, "fixture fonts should have been copied next to the tests");

        foreach (var path in fonts)
        {
            // Robustness contract: any SFNT the core loads, the engine must accept —
            // possibly with null/empty layout tables, never with an exception.
            var shaping = ShapingFont.Create(OpenTypeFont.LoadFromFile(path));
            _ = shaping.GetPlan(new Tag("latn"), ShapeDirection.LeftToRight);
        }
    }

    [Fact]
    public void GdefEmpty_ClassifiesNothing_AndMarkSetsMatchNothing()
    {
        GdefTable.Empty.GetGlyphClass(42).ShouldBe(GlyphClass.None);
        GdefTable.Empty.GetMarkAttachClass(42).ShouldBe(0);
        GdefTable.Empty.IsInMarkGlyphSet(0, 42).ShouldBeFalse();
    }

    [Fact]
    public void Coverage_BothFormats_RoundTripLookups()
    {
        // Handcrafted format 1: glyphs {5, 9, 12}.
        byte[] f1 = [0, 1, 0, 3, 0, 5, 0, 9, 0, 12];
        var c1 = Coverage.Parse(PadWithLeadingByte(f1, out var off1), off1);
        c1.GetCoverageIndex(5).ShouldBe(0);
        c1.GetCoverageIndex(9).ShouldBe(1);
        c1.GetCoverageIndex(12).ShouldBe(2);
        c1.GetCoverageIndex(6).ShouldBe(-1);

        // Handcrafted format 2: ranges {10..12 @0, 20..20 @3}.
        byte[] f2 = [0, 2, 0, 2, 0, 10, 0, 12, 0, 0, 0, 20, 0, 20, 0, 3];
        var c2 = Coverage.Parse(PadWithLeadingByte(f2, out var off2), off2);
        c2.GetCoverageIndex(10).ShouldBe(0);
        c2.GetCoverageIndex(12).ShouldBe(2);
        c2.GetCoverageIndex(20).ShouldBe(3);
        c2.GetCoverageIndex(13).ShouldBe(-1);
        c2.Contains(11).ShouldBeTrue();
    }

    [Fact]
    public void ClassDef_BothFormats_RoundTripLookups()
    {
        // Format 1: start glyph 8, classes [1, 2, 1].
        byte[] f1 = [0, 1, 0, 8, 0, 3, 0, 1, 0, 2, 0, 1];
        var c1 = ClassDef.Parse(PadWithLeadingByte(f1, out var off1), off1);
        c1.GetClass(8).ShouldBe(1);
        c1.GetClass(9).ShouldBe(2);
        c1.GetClass(10).ShouldBe(1);
        c1.GetClass(7).ShouldBe(0);
        c1.GetClass(11).ShouldBe(0);

        // Format 2: range 30..32 → class 4.
        byte[] f2 = [0, 2, 0, 1, 0, 30, 0, 32, 0, 4];
        var c2 = ClassDef.Parse(PadWithLeadingByte(f2, out var off2), off2);
        c2.GetClass(30).ShouldBe(4);
        c2.GetClass(32).ShouldBe(4);
        c2.GetClass(33).ShouldBe(0);
    }

    [Fact]
    public void MalformedCoverageAndClassDef_DegradeToEmpty()
    {
        // Offset 0 (spec: "no table"), out-of-range offset, unknown format — all Empty.
        Coverage.Parse([0, 1, 0, 0], 0).GetCoverageIndex(1).ShouldBe(-1);
        Coverage.Parse([0, 1], 40).GetCoverageIndex(1).ShouldBe(-1);
        Coverage.Parse(PadWithLeadingByte([0, 9, 0, 0], out var off), off).GetCoverageIndex(1).ShouldBe(-1);
        ClassDef.Parse([0, 1], 40).GetClass(1).ShouldBe(0);
    }

    /// <summary>Coverage/ClassDef offsets are subtable-relative and 0 means absent, so
    /// hand-built tables get one pad byte and parse at offset 1 — exercising the
    /// offset-relative slicing the same way real subtables do.</summary>
    private static byte[] PadWithLeadingByte(byte[] table, out int offset)
    {
        offset = 1;
        var padded = new byte[table.Length + 1];
        table.CopyTo(padded, 1);
        return padded;
    }
}

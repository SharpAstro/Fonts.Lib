using SharpAstro.Fonts.IO;

namespace SharpAstro.Fonts.Tests;

public class SfntDirectoryTests
{
    public static TheoryData<string> AllFixtures()
    {
        var data = new TheoryData<string>();
        foreach (var f in Fixtures.All) data.Add(f);
        return data;
    }

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void EveryFixture_HasParsableDirectory(string fontFile)
    {
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(fontFile));

        font.Directory.Tables.Count.ShouldBeGreaterThan(0);
        font.Directory.Tables.ShouldContainKey(Tags.Head);
        font.Directory.Tables.ShouldContainKey(Tags.Maxp);
        font.Directory.Tables.ShouldContainKey(Tags.Cmap);

        font.NumGlyphs.ShouldBeGreaterThan((ushort)0);
        font.UnitsPerEm.ShouldBeGreaterThan((ushort)0);
    }

    [Fact]
    public void DejaVuSans_IsTrueType()
    {
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.DejaVuSans));
        font.Directory.IsTrueType.ShouldBeTrue();
        font.Directory.IsCff.ShouldBeFalse();
    }
}

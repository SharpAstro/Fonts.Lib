using System.Globalization;
using System.Text;
using SharpAstro.Fonts.IO;

namespace SharpAstro.Fonts.Shaping.Tests;

/// <summary>
/// H1 conformance: replay the real-HarfBuzz golden fixtures through the engine and
/// require an exact match on glyph ids, clusters, and positions. This is the whole
/// point of the fixture harness — the engine reads the same font tables HarfBuzz
/// does, so on its supported feature slice (H1: ligatures + kerning) the output must
/// be identical, not merely close.
///
/// <para>Cases needing features H1 hasn't built are skipped by content: any text with
/// a combining mark needs GPOS mark positioning (H2) and HarfBuzz's normalization
/// (which the engine deliberately doesn't do), so those can't match yet.</para>
/// </summary>
public class HbConformanceTests
{
    private static readonly string FixtureDir = Path.Combine(AppContext.BaseDirectory, "Fixtures");

    private static readonly Dictionary<string, ShapingFont> FontCache = new(StringComparer.Ordinal);

    private static ShapingFont GetFont(string file)
    {
        if (!FontCache.TryGetValue(file, out var f))
        {
            f = ShapingFont.Create(OpenTypeFont.LoadFromFile(Path.Combine(FixtureDir, file)));
            FontCache[file] = f;
        }
        return f;
    }

    /// <summary>H1 supports ligatures + kerning only; a combining mark in the text needs H2 + normalization.</summary>
    private static bool IsH1Supported(HbCase c)
    {
        if (c.Rtl) return false; // RTL shapers land in H4
        foreach (var rune in c.Text.EnumerateRunes())
            if (Rune.GetUnicodeCategory(rune) == UnicodeCategory.NonSpacingMark) return false;
        return true;
    }

    public static IEnumerable<object[]> H1Cases()
        => HbFixtures.LoadAll().Where(IsH1Supported).Select(c => new object[] { c });

    [Theory]
    [MemberData(nameof(H1Cases))]
    public void Engine_Matches_HarfBuzz(HbCase c)
    {
        var font = GetFont(c.Font);
        var hmtx = font.Font.Hmtx;
        hmtx.ShouldNotBeNull($"{c.Font} needs hmtx to compare advances");

        var buffer = new ShapeBuffer { Direction = c.Rtl ? ShapeDirection.RightToLeft : ShapeDirection.LeftToRight };
        buffer.AddText(c.Text);
        Shaper.Shape(font, buffer, new Tag(c.Script));

        buffer.Length.ShouldBe(c.Glyphs.Count,
            $"[{c.Text}] glyph count: engine=[{DumpGids(buffer)}] hb=[{string.Join(",", c.Glyphs.Select(g => g.Gid))}]");

        for (var i = 0; i < c.Glyphs.Count; i++)
        {
            var hb = c.Glyphs[i];
            buffer.GlyphIds[i].ShouldBe(hb.Gid, $"[{c.Text}] glyph #{i} id");
            buffer.Clusters[i].ShouldBe(hb.Cluster, $"[{c.Text}] glyph #{i} cluster");

            // Engine reports advance DELTA relative to hmtx; HarfBuzz reports the absolute advance.
            var engineAbsolute = hmtx.GetAdvanceWidth(buffer.GlyphIds[i]) + buffer.XAdvanceDeltas[i];
            engineAbsolute.ShouldBe(hb.XAdvance, $"[{c.Text}] glyph #{i} x-advance");
            buffer.XOffsets[i].ShouldBe(hb.XOffset, $"[{c.Text}] glyph #{i} x-offset");
            buffer.YOffsets[i].ShouldBe(hb.YOffset, $"[{c.Text}] glyph #{i} y-offset");
        }
    }

    private static string DumpGids(ShapeBuffer b)
    {
        var ids = new uint[b.Length];
        b.GlyphIds.CopyTo(ids);
        return string.Join(",", ids);
    }

    [Fact]
    public void H1Cases_AreActuallyExercised()
    {
        // Guard against the filter silently excluding everything (e.g. fixtures not copied).
        H1Cases().Count().ShouldBeGreaterThan(4, "expected several ligature/kerning cases to replay");
    }
}

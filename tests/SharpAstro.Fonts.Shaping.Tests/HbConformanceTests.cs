using SharpAstro.Fonts.IO;

namespace SharpAstro.Fonts.Shaping.Tests;

/// <summary>
/// Conformance: replay the real-HarfBuzz golden fixtures through the engine and require
/// an exact match on glyph ids, clusters, and positions. This is the whole point of the
/// fixture harness — the engine reads the same font tables HarfBuzz does, so on its
/// supported feature slice the output must be identical, not merely close. H1 proved
/// ligatures + kerning; H2 adds mark positioning, so the base+combining-mark fixtures
/// (e.g. <c>q</c>+U+0301) now replay here too.
///
/// <para>Only RTL cases are held back (the Arabic/bidi shapers land in H4). The fixtures
/// deliberately avoid base+mark pairs that HarfBuzz would compose to a precomposed glyph
/// — the engine does no normalization, so those can't match (see <see cref="HbFixtures"/>
/// remarks).</para>
/// </summary>
public class HbConformanceTests
{
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

    private static readonly string FixtureDir = Path.Combine(AppContext.BaseDirectory, "Fixtures");

    /// <summary>LTR cases are the engine's supported slice; RTL shapers arrive in H4.</summary>
    private static bool IsSupported(HbCase c) => !c.Rtl;

    public static IEnumerable<object[]> SupportedCases()
        => HbFixtures.LoadAll().Where(IsSupported).Select(c => new object[] { c });

    [Theory]
    [MemberData(nameof(SupportedCases))]
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
    public void SupportedCases_ExerciseLigaturesKerningAndMarks()
    {
        // Guard against the filter silently excluding everything (e.g. fixtures not copied)
        // and against the mark fixtures being dropped in a regeneration.
        var cases = SupportedCases().Select(o => (HbCase)o[0]).ToList();
        cases.Count.ShouldBeGreaterThan(4, "expected several ligature/kerning cases to replay");

        // A base+mark case must be present and must actually carry a nonzero mark offset,
        // or "mark positioning works" isn't being tested.
        var markCases = cases.Where(c => c.Glyphs.Count >= 2
            && c.Glyphs.Skip(1).Any(g => g.XOffset != 0 || g.YOffset != 0)).ToList();
        markCases.Count.ShouldBeGreaterThan(0, "expected base+mark fixtures with real GPOS offsets");
    }
}

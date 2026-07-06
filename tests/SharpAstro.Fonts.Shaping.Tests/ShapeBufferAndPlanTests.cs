using SharpAstro.Fonts.IO;

namespace SharpAstro.Fonts.Shaping.Tests;

/// <summary>
/// H0 contracts for the buffer (cluster convention, reuse, RTL reversal) and the
/// plan cache (resolution, ordering, dedup, caching). Clusters are UTF-16 offsets —
/// the exact convention DIR.Lib's A2 <c>ShapedGlyph.Cluster</c> uses, which A4's
/// caret mapping will depend on.
/// </summary>
public class ShapeBufferAndPlanTests
{
    private static readonly string FixtureDir = Path.Combine(AppContext.BaseDirectory, "Fixtures");

    private static ShapingFont LoadDejaVu()
        => ShapingFont.Create(OpenTypeFont.LoadFromFile(Path.Combine(FixtureDir, "DejaVuSans.ttf")));

    [Fact]
    public void AddText_RecordsUtf16Clusters_IncludingAstralPairs()
    {
        var buffer = new ShapeBuffer();
        buffer.AddText("Hi\U0001F600!"); // emoji = surrogate pair (2 UTF-16 units)

        buffer.Length.ShouldBe(4);
        buffer.Clusters[0].ShouldBe(0);
        buffer.Clusters[1].ShouldBe(1);
        buffer.Clusters[2].ShouldBe(2);
        buffer.Clusters[3].ShouldBe(4); // '!' lands after the 2-unit pair

        buffer.GlyphIds[2].ShouldBe(0x1F600u); // codepoints until Shape maps them
    }

    [Fact]
    public void AddText_ClusterOffset_BiasesClusters_ForRunSlicing()
    {
        // An itemizer (H5) slices a line into runs and shapes each with the run's
        // start offset, so clusters keep indexing the full line.
        var buffer = new ShapeBuffer();
        buffer.AddText("cd".AsSpan(), clusterOffset: 2);
        buffer.Clusters[0].ShouldBe(2);
        buffer.Clusters[1].ShouldBe(3);
    }

    [Fact]
    public void Clear_ResetsLength_AndBufferIsReusable()
    {
        var buffer = new ShapeBuffer();
        buffer.AddText("abc");
        buffer.Clear();
        buffer.Length.ShouldBe(0);
        buffer.AddText("x");
        buffer.Length.ShouldBe(1);
        buffer.Clusters[0].ShouldBe(0);
    }

    [Fact]
    public void AddText_GrowsPastInitialCapacity()
    {
        var buffer = new ShapeBuffer();
        var text = new string('a', 300); // > the initial 64-slot capacity
        buffer.AddText(text);
        buffer.Length.ShouldBe(300);
        buffer.Clusters[299].ShouldBe(299);
    }

    [Fact]
    public void Shape_RightToLeft_ReversesToVisualOrder()
    {
        var font = LoadDejaVu();
        var buffer = new ShapeBuffer { Direction = ShapeDirection.RightToLeft };
        buffer.AddText("AB");

        Shaper.Shape(font, buffer, new Tag("latn"));

        buffer.GlyphIds[0].ShouldBe(font.Font.GetGlyphId('B'));
        buffer.GlyphIds[1].ShouldBe(font.Font.GetGlyphId('A'));
        buffer.Clusters[0].ShouldBe(1); // clusters travel with their glyphs
        buffer.Clusters[1].ShouldBe(0);
    }

    [Fact]
    public void GetPlan_IsCached_AndResolvesLookups()
    {
        var font = LoadDejaVu();
        var latn = new Tag("latn");

        var plan1 = font.GetPlan(latn, ShapeDirection.LeftToRight);
        var plan2 = font.GetPlan(latn, ShapeDirection.LeftToRight);
        ReferenceEquals(plan1, plan2).ShouldBeTrue("plans must be cached per (script, direction)");

        // DejaVu latn references liga (GSUB) and kern/mark (GPOS) lookups.
        plan1.SubstitutionLookups.Length.ShouldBeGreaterThan(0);
        plan1.PositioningLookups.Length.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Plan_LookupsSortedByIndex_NoDuplicates()
    {
        var font = LoadDejaVu();
        var plan = font.GetPlan(new Tag("latn"), ShapeDirection.LeftToRight);

        foreach (var lookups in new[] { plan.SubstitutionLookups, plan.PositioningLookups })
        {
            for (var i = 1; i < lookups.Length; i++)
            {
                // Strictly ascending ⇒ spec application order AND per-index dedup.
                lookups[i].LookupIndex.ShouldBeGreaterThan(lookups[i - 1].LookupIndex);
            }
        }
    }

    [Fact]
    public void GetPlan_UnknownScript_FallsBackToDflt_OrEmpty()
    {
        var font = LoadDejaVu();
        // Must not throw regardless of whether DejaVu has a DFLT script entry.
        var plan = font.GetPlan(new Tag("zxzx"), ShapeDirection.LeftToRight);
        plan.ShouldNotBeNull();
    }
}

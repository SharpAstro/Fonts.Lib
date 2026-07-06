using SharpAstro.Fonts.IO;

namespace SharpAstro.Fonts.Shaping.Tests;

/// <summary>
/// The H0 skeleton contract: with no lookup types implemented, shaping must equal
/// plain per-codepoint cmap mapping with zero position deltas — the same glyph
/// stream <see cref="OpenTypeFont.GetGlyphId(uint)"/> produces. This is the
/// baseline every H1+ stage builds on (and the all-features-off equivalence the
/// A2 <c>AdvanceShaper</c> parity argument rests on).
/// </summary>
public class ShaperSkeletonTests
{
    private static readonly string FixtureDir = Path.Combine(AppContext.BaseDirectory, "Fixtures");

    private static ShapingFont Load(string name)
        => ShapingFont.Create(OpenTypeFont.LoadFromFile(Path.Combine(FixtureDir, name)));

    [Fact]
    public void Shape_Ltr_EqualsCmapMapping_WithZeroDeltas()
    {
        var font = Load("DejaVuSans.ttf");
        var buffer = new ShapeBuffer();
        const string text = "Waffle fi AV";
        buffer.AddText(text);

        Shaper.Shape(font, buffer, new Tag("latn"));

        buffer.Length.ShouldBe(text.Length); // BMP-only text: one glyph per char in H0
        var cluster = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var i = cluster; // BMP text ⇒ cluster == index
            buffer.GlyphIds[i].ShouldBe(font.Font.GetGlyphId((uint)rune.Value));
            buffer.Clusters[i].ShouldBe(cluster);
            buffer.XAdvanceDeltas[i].ShouldBe(0);
            buffer.XOffsets[i].ShouldBe(0);
            buffer.YOffsets[i].ShouldBe(0);
            cluster += rune.Utf16SequenceLength;
        }
    }

    [Fact]
    public void Shape_UnmappedCodepoint_YieldsNotdef()
    {
        var font = Load("DejaVuSans.ttf");
        var buffer = new ShapeBuffer();
        buffer.AddText("\U0010FFFD"); // private-use plane 16 — not in any text font

        Shaper.Shape(font, buffer, new Tag("latn"));

        buffer.Length.ShouldBe(1);
        buffer.GlyphIds[0].ShouldBe(0u);
    }

    [Fact]
    public void Shape_EmptyBuffer_IsANoOp()
    {
        var font = Load("DejaVuSans.ttf");
        var buffer = new ShapeBuffer();
        Shaper.Shape(font, buffer, new Tag("latn"));
        buffer.Length.ShouldBe(0);
    }

    [Fact]
    public void Shape_FontWithoutLayoutTables_StillMapsGlyphs()
    {
        // Merida has minimal/no OTL tables — the pipeline must degrade to cmap mapping.
        var font = Load("Merida.ttf");
        var buffer = new ShapeBuffer();
        buffer.AddText("abc");

        Shaper.Shape(font, buffer, new Tag("latn"));

        buffer.Length.ShouldBe(3);
        buffer.GlyphIds[0].ShouldBe(font.Font.GetGlyphId('a'));
    }
}

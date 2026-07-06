using SharpAstro.Fonts.IO;

namespace SharpAstro.Fonts.Shaping.Tests;

/// <summary>
/// Pipeline-level contracts that hold regardless of which lookup types are
/// implemented — graceful degradation. A font with no GSUB/GPOS passes straight
/// through as exact cmap mapping with zero position deltas (the pure pass-through
/// underpinning the A2 <c>AdvanceShaper</c>-parity argument); an unmapped codepoint
/// becomes .notdef; an empty buffer is a no-op. Real shaping (ligatures, kerning) is
/// proven exactly against HarfBuzz in <see cref="HbConformanceTests"/>.
/// </summary>
public class ShaperSkeletonTests
{
    private static readonly string FixtureDir = Path.Combine(AppContext.BaseDirectory, "Fixtures");

    private static ShapingFont Load(string name)
        => ShapingFont.Create(OpenTypeFont.LoadFromFile(Path.Combine(FixtureDir, name)));

    [Fact]
    public void Shape_FontWithoutLayoutTables_IsExactCmapIdentity_WithZeroDeltas()
    {
        var font = Load("Merida.ttf");
        // The pass-through guarantee is only meaningful for a font the engine can't shape.
        // Merida ships neither GSUB nor GPOS; assert that, so the test fails loudly if the
        // fixture ever changes rather than silently becoming vacuous.
        font.HasSubstitution.ShouldBeFalse("Merida is the no-layout-tables fixture");
        font.HasPositioning.ShouldBeFalse("Merida is the no-layout-tables fixture");

        var buffer = new ShapeBuffer();
        const string text = "Waffle fi AV"; // ligature/kern pairs in a normal font — inert here
        buffer.AddText(text);

        Shaper.Shape(font, buffer, new Tag("latn"));

        buffer.Length.ShouldBe(text.Length); // no substitution → one glyph per BMP char
        var cluster = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            buffer.GlyphIds[cluster].ShouldBe(font.Font.GetGlyphId((uint)rune.Value));
            buffer.Clusters[cluster].ShouldBe(cluster);
            buffer.XAdvanceDeltas[cluster].ShouldBe(0);
            buffer.XOffsets[cluster].ShouldBe(0);
            buffer.YOffsets[cluster].ShouldBe(0);
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
}

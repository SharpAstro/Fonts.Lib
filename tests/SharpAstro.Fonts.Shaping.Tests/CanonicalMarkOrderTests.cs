using SharpAstro.Fonts.IO;
using SharpAstro.Fonts.Shaping.Ucd;

namespace SharpAstro.Fonts.Shaping.Tests;

/// <summary>
/// Canonical combining class table and the mark reordering it drives. The class-value
/// assertions guard the generated UCD-wide table (see <c>tools/UcdGen</c>) against a bad
/// regeneration (a wrong value would silently mis-order stacked marks); the end-to-end test
/// proves a non-canonically-typed mark sequence comes out in canonical order — the same result
/// the HarfBuzz conformance fixture checks, but stated here as intent.
/// </summary>
public class CanonicalMarkOrderTests
{
    [Theory]
    [InlineData(0x0300, 230)] // combining grave (above)
    [InlineData(0x0301, 230)] // combining acute (above)
    [InlineData(0x0316, 220)] // combining grave below
    [InlineData(0x0323, 220)] // combining dot below
    [InlineData(0x0327, 202)] // combining cedilla (attached below)
    [InlineData(0x031B, 216)] // combining horn (attached above-right)
    [InlineData(0x0334, 1)]   // combining tilde overlay
    [InlineData(0x0345, 240)] // combining greek ypogegrammeni (iota subscript)
    [InlineData(0x036F, 230)] // last codepoint in the block
    [InlineData(0x034F, 0)]   // combining grapheme joiner — a starter, blocks reordering
    [InlineData(0x0041, 0)]   // 'A' — a base letter, not a mark
    [InlineData(0x0370, 0)]   // Greek capital heta — a base letter, CCC 0
    public void Ccc_MatchesUnicode(int codepoint, int expected)
        => CanonicalCombiningClass.Get((uint)codepoint).ShouldBe((byte)expected);

    [Fact]
    public void Shape_ReordersMarksIntoCanonicalOrder()
    {
        var font = ShapingFont.Create(OpenTypeFont.LoadFromFile(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "DejaVuSans.ttf")));
        var acute = font.Font.GetGlyphId(0x0301);    // CCC 230 (above)
        var dotBelow = font.Font.GetGlyphId(0x0323);  // CCC 220 (below)

        // Typed NON-canonically: above mark before below mark. 'q' has no precomposed forms,
        // so nothing composes and both marks survive as separate glyphs.
        var buffer = new ShapeBuffer();
        buffer.AddText("q̣́");
        Shaper.Shape(font, buffer, new Tag("latn"));

        var gids = buffer.GlyphIds.ToArray();
        var idxBelow = Array.IndexOf(gids, dotBelow);
        var idxAbove = Array.IndexOf(gids, acute);
        idxBelow.ShouldBeGreaterThan(0, "the below mark should survive as its own glyph");
        idxAbove.ShouldBeGreaterThan(0, "the above mark should survive as its own glyph");
        idxBelow.ShouldBeLessThan(idxAbove,
            "canonical ordering places the below mark (CCC 220) before the above mark (CCC 230)");
    }
}

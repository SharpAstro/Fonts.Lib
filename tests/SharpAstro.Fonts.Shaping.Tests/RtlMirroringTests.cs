using SharpAstro.Fonts.IO;

namespace SharpAstro.Fonts.Shaping.Tests;

/// <summary>
/// End-to-end bidi mirroring: in a right-to-left run the shaper remaps mirrorable characters to
/// their Bidi_Mirroring_Glyph counterpart before cmap, so an opening parenthesis renders with
/// the closing glyph (HarfBuzz's mirror pass). LTR runs are left untouched. The HarfBuzz
/// conformance fixtures cover Arabic joining and Hebrew reversal but contain no bracket, so this
/// is the dedicated check for the mirror step.
/// </summary>
public class RtlMirroringTests
{
    private static ShapingFont Font()
        => ShapingFont.Create(OpenTypeFont.LoadFromFile(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "DejaVuSans.ttf")));

    [Fact]
    public void RtlRun_MirrorsOpeningParen_ToClosingGlyph()
    {
        var font = Font();
        var openGlyph = font.Font.GetGlyphId(0x0028);  // '('
        var closeGlyph = font.Font.GetGlyphId(0x0029); // ')'
        closeGlyph.ShouldNotBe(openGlyph, "fixture must have distinct '(' and ')' glyphs");

        var buffer = new ShapeBuffer { Direction = ShapeDirection.RightToLeft };
        buffer.AddText("(");
        Shaper.Shape(font, buffer, new Tag("latn"));

        buffer.Length.ShouldBe(1);
        buffer.GlyphIds[0].ShouldBe(closeGlyph, "an RTL '(' should be mirrored and render as ')'");
    }

    [Fact]
    public void LtrRun_DoesNotMirror()
    {
        var font = Font();
        var openGlyph = font.Font.GetGlyphId(0x0028);

        var buffer = new ShapeBuffer { Direction = ShapeDirection.LeftToRight };
        buffer.AddText("(");
        Shaper.Shape(font, buffer, new Tag("latn"));

        buffer.Length.ShouldBe(1);
        buffer.GlyphIds[0].ShouldBe(openGlyph, "an LTR '(' must not be mirrored");
    }
}

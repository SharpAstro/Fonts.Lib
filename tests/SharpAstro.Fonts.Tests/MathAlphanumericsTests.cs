namespace SharpAstro.Fonts.Tests;

/// <summary>
/// Tests for <see cref="MathAlphanumerics.MapCodepoint"/> — the pure
/// Unicode lookup that turns (base codepoint, style) into a styled
/// math-alphanumeric codepoint, with the U+2100 letter-like-symbols
/// holes resolved.
/// </summary>
public sealed class MathAlphanumericsTests
{
    /// <summary>Normal style is the identity for any codepoint.</summary>
    [Theory]
    [InlineData('A')]
    [InlineData('z')]
    [InlineData(0x03B1u)] // α
    [InlineData('5')]
    [InlineData(0xFFFFu)] // arbitrary unrelated codepoint
    public void Normal_IsIdentity(uint codepoint)
        => MathAlphanumerics.MapCodepoint(codepoint, MathStyle.Normal).ShouldBe(codepoint);

    /// <summary>Italic Latin A maps to U+1D434 (the start of the
    /// italic-uppercase block); italic Z maps to U+1D44D.</summary>
    [Fact]
    public void Italic_LatinUpper_MapsToBlockStart()
    {
        MathAlphanumerics.MapCodepoint('A', MathStyle.Italic).ShouldBe(0x1D434u);
        MathAlphanumerics.MapCodepoint('Z', MathStyle.Italic).ShouldBe(0x1D44Du);
    }

    /// <summary>The famous hole: italic <c>h</c> at U+1D455 redirects to
    /// U+210E (PLANCK CONSTANT) per Unicode.</summary>
    [Fact]
    public void Italic_LowercaseH_RoutesToPlanckConstant()
        => MathAlphanumerics.MapCodepoint('h', MathStyle.Italic).ShouldBe(0x210Eu);

    /// <summary>Other italic lowercase letters land in the U+1D44E block
    /// without redirection.</summary>
    [Theory]
    [InlineData('a', 0x1D44Eu)]
    [InlineData('g', 0x1D454u)] // one before the h hole
    [InlineData('i', 0x1D456u)] // one after the h hole
    [InlineData('z', 0x1D467u)]
    public void Italic_LatinLower_MapsToBlock(uint codepoint, uint expected)
        => MathAlphanumerics.MapCodepoint(codepoint, MathStyle.Italic).ShouldBe(expected);

    /// <summary>All seven script-uppercase holes redirect to the
    /// Letterlike Symbols block.</summary>
    [Theory]
    [InlineData('B', 0x212Cu)]
    [InlineData('E', 0x2130u)]
    [InlineData('F', 0x2131u)]
    [InlineData('H', 0x210Bu)]
    [InlineData('I', 0x2110u)]
    [InlineData('L', 0x2112u)]
    [InlineData('M', 0x2133u)]
    [InlineData('R', 0x211Bu)]
    public void Script_UppercaseHoles_RouteToLetterlike(uint codepoint, uint expected)
        => MathAlphanumerics.MapCodepoint(codepoint, MathStyle.Script).ShouldBe(expected);

    /// <summary>Non-hole script-uppercase letters land in U+1D49C block.</summary>
    [Fact]
    public void Script_A_LandsInBlock()
        => MathAlphanumerics.MapCodepoint('A', MathStyle.Script).ShouldBe(0x1D49Cu);

    /// <summary>Double-struck holes (U+2100 letterlike forms).</summary>
    [Theory]
    [InlineData('C', 0x2102u)]
    [InlineData('H', 0x210Du)]
    [InlineData('N', 0x2115u)]
    [InlineData('P', 0x2119u)]
    [InlineData('Q', 0x211Au)]
    [InlineData('R', 0x211Du)]
    [InlineData('Z', 0x2124u)]
    public void DoubleStruck_UppercaseHoles_RouteToLetterlike(uint codepoint, uint expected)
        => MathAlphanumerics.MapCodepoint(codepoint, MathStyle.DoubleStruck).ShouldBe(expected);

    /// <summary>Italic Greek upper Α/Ω land at U+1D6E2/U+1D6FA (the
    /// boundaries of the italic Greek-uppercase block).</summary>
    [Fact]
    public void Italic_GreekUpper_MapsToBlock()
    {
        MathAlphanumerics.MapCodepoint(0x0391u, MathStyle.Italic).ShouldBe(0x1D6E2u); // Α
        MathAlphanumerics.MapCodepoint(0x03A9u, MathStyle.Italic).ShouldBe(0x1D6FAu); // Ω
    }

    /// <summary>Italic Greek lower α/ω.</summary>
    [Fact]
    public void Italic_GreekLower_MapsToBlock()
    {
        MathAlphanumerics.MapCodepoint(0x03B1u, MathStyle.Italic).ShouldBe(0x1D6FCu); // α
        // ω is the 25th letter (offset 24) in α-ω. The seven slots after
        // ω at U+1D715–U+1D71B hold the variant Greek symbols (∂ϵϑϰϕϱϖ),
        // not extra Greek letters — so the lowercase block ends at ω.
        MathAlphanumerics.MapCodepoint(0x03C9u, MathStyle.Italic).ShouldBe(0x1D714u); // ω
    }

    /// <summary>Bold digits map to U+1D7CE block.</summary>
    [Theory]
    [InlineData('0', 0x1D7CEu)]
    [InlineData('9', 0x1D7D7u)]
    public void Bold_Digits_MapToBlock(uint codepoint, uint expected)
        => MathAlphanumerics.MapCodepoint(codepoint, MathStyle.Bold).ShouldBe(expected);

    /// <summary>Combinations Unicode never assigned: italic digits,
    /// Fraktur Greek, monospace Greek. Each returns null so callers
    /// can fall back to the unstyled codepoint.</summary>
    [Theory]
    [InlineData('5', MathStyle.Italic)]              // no italic digits
    [InlineData(0x0391u, MathStyle.Fraktur)]         // Fraktur Greek doesn't exist
    [InlineData(0x03B1u, MathStyle.Monospace)]       // monospace Greek doesn't exist
    [InlineData(0x03B1u, MathStyle.DoubleStruck)]    // double-struck Greek doesn't exist
    public void UnassignedCombinations_ReturnNull(uint codepoint, MathStyle style)
        => MathAlphanumerics.MapCodepoint(codepoint, style).ShouldBeNull();

    /// <summary>Codepoints outside the supported ranges (CJK, emoji,
    /// punctuation) return null in any non-Normal style.</summary>
    [Theory]
    [InlineData(0x4E00u)] // 一 (CJK)
    [InlineData(0x002Bu)] // '+'
    [InlineData(0x2200u)] // ∀
    public void OutOfRange_ReturnsNull(uint codepoint)
        => MathAlphanumerics.MapCodepoint(codepoint, MathStyle.Italic).ShouldBeNull();

    /// <summary>
    /// Real-font sanity: DejaVu Sans is a body-text font and doesn't
    /// ship the math-alphanumerics block. <see cref="OpenTypeFont.GetMathVariantGlyphId"/>
    /// must return 0 (cmap miss), not throw or return a wrong glyph id.
    /// This is the load-bearing case for the "fallback to original
    /// codepoint" path that consumers rely on.
    /// </summary>
    [Fact]
    public void GetMathVariantGlyphId_NonMathFont_Returns0()
    {
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.DejaVuSans));
        font.GetMathVariantGlyphId('A', MathStyle.Italic).ShouldBe(0u);
        font.GetMathVariantGlyphId('z', MathStyle.Bold).ShouldBe(0u);
    }

    /// <summary>Style combinations Unicode never assigned go through
    /// the same null-from-MapCodepoint path; <see cref="OpenTypeFont.GetMathVariantGlyphId"/>
    /// returns 0 without consulting the cmap.</summary>
    [Fact]
    public void GetMathVariantGlyphId_UnassignedStyle_Returns0()
    {
        var font = OpenTypeFont.LoadFromFile(Fixtures.Path(Fixtures.DejaVuSans));
        font.GetMathVariantGlyphId('5', MathStyle.Italic).ShouldBe(0u);
    }
}

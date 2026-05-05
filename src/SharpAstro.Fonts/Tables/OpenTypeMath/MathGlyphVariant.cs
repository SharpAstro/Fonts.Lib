namespace SharpAstro.Fonts.Tables.OpenTypeMath;

/// <summary>
/// One pre-drawn larger size of a stretchable glyph (radical, brace, paren etc.).
/// The font ships a chain of these per stretch direction; consumers walk the
/// chain picking the smallest variant whose <see cref="AdvanceMeasurement"/>
/// covers the required content height (vertical) or width (horizontal).
/// Beyond the largest variant, the assembly recipe in
/// <see cref="MathGlyphAssembly"/> takes over.
/// </summary>
/// <param name="GlyphId">Glyph id of this variant in the font's glyph table.</param>
/// <param name="AdvanceMeasurement">Advance height (vertical) or width
/// (horizontal) of this variant, in FUnits.</param>
public readonly record struct MathGlyphVariant(ushort GlyphId, ushort AdvanceMeasurement);

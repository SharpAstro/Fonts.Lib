namespace SharpAstro.Fonts.Shaping.Ucd;

/// <summary>
/// Bidi_Mirroring_Glyph (BidiMirroring.txt): the codepoint whose glyph mirrors this one's when
/// laid out right-to-left — e.g. <c>'(' ⇄ ')'</c>, <c>'&lt;' ⇄ '&gt;'</c>. A default (Latin,
/// Hebrew, Arabic) shaper remaps mirrorable characters in an RTL run before cmap, matching
/// HarfBuzz's mirroring pass. Codepoints without a mirror are returned unchanged.
/// </summary>
internal static partial class BidiMirroring
{
    /// <summary>The mirror of <paramref name="codepoint"/> for RTL layout, or
    /// <paramref name="codepoint"/> itself if it has no mirror.</summary>
    public static uint Get(uint codepoint) => UcdTables.PairValue(Pairs, codepoint, notFound: codepoint);
}

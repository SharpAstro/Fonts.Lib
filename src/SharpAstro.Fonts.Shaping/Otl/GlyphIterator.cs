namespace SharpAstro.Fonts.Shaping.Otl;

/// <summary>
/// Skip-aware traversal of a <see cref="ShapeBuffer"/> for lookup application: within
/// a lookup, glyphs its <see cref="LookupFlags"/> ignore (marks, bases, ligatures per
/// the flags + GDEF class) are invisible to sequence matching — a ligature's components
/// or a pair's second glyph are found across them. Mirrors HarfBuzz's
/// <c>hb_ot_apply_context_t::skipping_iterator_t</c>. Only class-based skipping is applied
/// here (not the per-glyph feature mask, which gates whether a lookup starts at a
/// position — HarfBuzz likewise ignores the mask for intermediate matched glyphs).
/// </summary>
internal static class GlyphIterator
{
    /// <summary>First index &gt; <paramref name="from"/> the lookup doesn't skip, or −1 if none.</summary>
    public static int Next(ShapeBuffer buffer, GdefTable gdef, LookupFlags flags, int markFilteringSet, int from)
    {
        var glyphs = buffer.GlyphsMutable;
        var classes = buffer.ClassesMutable;
        for (var k = from + 1; k < glyphs.Length; k++)
        {
            if (!flags.SkipsGlyph(gdef, glyphs[k], (GlyphClass)classes[k], markFilteringSet))
                return k;
        }
        return -1;
    }

    /// <summary>Last index &lt; <paramref name="from"/> the lookup doesn't skip, or −1 if none.</summary>
    public static int Prev(ShapeBuffer buffer, GdefTable gdef, LookupFlags flags, int markFilteringSet, int from)
    {
        var glyphs = buffer.GlyphsMutable;
        var classes = buffer.ClassesMutable;
        for (var k = from - 1; k >= 0; k--)
        {
            if (!flags.SkipsGlyph(gdef, glyphs[k], (GlyphClass)classes[k], markFilteringSet))
                return k;
        }
        return -1;
    }
}

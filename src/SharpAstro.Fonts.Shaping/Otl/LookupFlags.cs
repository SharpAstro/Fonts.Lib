namespace SharpAstro.Fonts.Shaping.Otl;

/// <summary>
/// OpenType lookupFlag bits. The low byte is boolean skips; the high byte
/// (<see cref="MarkAttachmentTypeMask"/>) restricts processing to marks of one
/// GDEF mark-attachment class when non-zero.
/// </summary>
[Flags]
internal enum LookupFlags : ushort
{
    None = 0,
    /// <summary>Cursive-attachment baseline behavior for RTL (GPOS type 3); not a skip flag.</summary>
    RightToLeft = 0x0001,
    IgnoreBaseGlyphs = 0x0002,
    IgnoreLigatures = 0x0004,
    IgnoreMarks = 0x0008,
    /// <summary>Process only marks in the lookup's mark-filtering set (GDEF MarkGlyphSets).</summary>
    UseMarkFilteringSet = 0x0010,
    /// <summary>Non-zero: process only marks whose GDEF mark-attachment class equals this value.</summary>
    MarkAttachmentTypeMask = 0xFF00,
}

internal static class LookupFlagsExtensions
{
    /// <summary>
    /// Whether a lookup with <paramref name="flags"/> (+ <paramref name="markFilteringSet"/>
    /// when <see cref="LookupFlags.UseMarkFilteringSet"/> is set) skips the glyph
    /// <paramref name="glyphId"/> of GDEF class <paramref name="glyphClass"/>. Skipped
    /// glyphs are invisible to the lookup — both as the current glyph and inside
    /// sequence matching (ligature components, pair second glyphs, context strings).
    /// </summary>
    public static bool SkipsGlyph(this LookupFlags flags, GdefTable gdef,
        uint glyphId, GlyphClass glyphClass, int markFilteringSet)
    {
        switch (glyphClass)
        {
            case GlyphClass.Base:
                return (flags & LookupFlags.IgnoreBaseGlyphs) != 0;
            case GlyphClass.Ligature:
                return (flags & LookupFlags.IgnoreLigatures) != 0;
            case GlyphClass.Mark:
                if ((flags & LookupFlags.IgnoreMarks) != 0) return true;
                if ((flags & LookupFlags.UseMarkFilteringSet) != 0)
                    return !gdef.IsInMarkGlyphSet(markFilteringSet, glyphId);
                var attachType = (int)(flags & LookupFlags.MarkAttachmentTypeMask) >> 8;
                if (attachType != 0)
                    return gdef.GetMarkAttachClass(glyphId) != attachType;
                return false;
            default:
                // Unclassified / component glyphs are never skipped by class flags.
                return false;
        }
    }
}

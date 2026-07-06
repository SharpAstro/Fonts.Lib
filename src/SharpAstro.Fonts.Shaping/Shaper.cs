using SharpAstro.Fonts.IO;
using SharpAstro.Fonts.Shaping.Otl;

namespace SharpAstro.Fonts.Shaping;

/// <summary>
/// The shaping entry point: maps a <see cref="ShapeBuffer"/>'s codepoints to glyphs
/// and runs the font's GSUB/GPOS lookups per the resolved <see cref="ShapePlan"/>.
///
/// <para><b>H0 status:</b> the full pipeline runs — cmap mapping, GDEF glyph
/// classing, RTL reversal, plan resolution, and the lookup walk with lookupFlag
/// skipping — but no lookup <em>types</em> are implemented yet, so every subtable
/// application is a no-op and output equals per-codepoint cmap mapping with zero
/// position deltas. H1 fills in GSUB 1/4 + GPOS 1/2 (ligatures + kerning).</para>
/// </summary>
public static class Shaper
{
    /// <summary>
    /// Shape one single-script, single-direction run in place. The buffer must have
    /// been filled with <see cref="ShapeBuffer.AddText"/>; on return it holds glyph
    /// ids (visual order for RTL), clusters, and position deltas in font units.
    /// <paramref name="script"/> is an OpenType script tag (e.g. <c>latn</c>,
    /// <c>arab</c>); fonts without the script fall back to <c>DFLT</c>.
    /// </summary>
    public static void Shape(ShapingFont font, ShapeBuffer buffer, Tag script)
    {
        ArgumentNullException.ThrowIfNull(font);
        ArgumentNullException.ThrowIfNull(buffer);
        if (buffer.Length == 0) return;

        MapToGlyphs(font, buffer);

        if (buffer.Direction == ShapeDirection.RightToLeft)
            buffer.Reverse();

        var plan = font.GetPlan(script, buffer.Direction);
        if (font.Gsub is not null)
            ApplyLookups(font.Gsub, plan.SubstitutionLookups, font, buffer, isSubstitution: true);
        if (font.Gpos is not null)
            ApplyLookups(font.Gpos, plan.PositioningLookups, font, buffer, isSubstitution: false);
    }

    /// <summary>Codepoints → glyph ids via cmap, and GDEF glyph classes for lookupFlag skipping.</summary>
    private static void MapToGlyphs(ShapingFont font, ShapeBuffer buffer)
    {
        var glyphs = buffer.GlyphsMutable;
        var classes = buffer.ClassesMutable;
        for (var i = 0; i < glyphs.Length; i++)
        {
            var gid = font.Font.GetGlyphId(glyphs[i]);
            glyphs[i] = gid;
            // Fonts without GDEF leave classes at None — never skipped by class flags.
            // (H2 will synthesize Mark from Unicode general category when GDEF is absent,
            // which mark positioning needs; irrelevant while no lookup types apply.)
            classes[i] = (byte)font.Gdef.GetGlyphClass(gid);
        }
    }

    /// <summary>
    /// The spec's application model: each planned lookup runs over the whole run in
    /// lookup-index order; within a lookup, glyphs it skips (mask miss or lookupFlag
    /// class skip) are invisible; the first subtable that applies at a position wins
    /// and the walk continues after the applied sequence.
    /// </summary>
    private static void ApplyLookups(LayoutTable table,
        ShapePlan.PlannedLookup[] lookups, ShapingFont font, ShapeBuffer buffer,
        bool isSubstitution)
    {
        foreach (var planned in lookups)
        {
            var lookup = table.Lookups[planned.LookupIndex];
            if (lookup.Subtables.Length == 0) continue;

            for (var i = 0; i < buffer.Length;)
            {
                if ((buffer.MasksMutable[i] & planned.Mask) == 0
                    || lookup.Flags.SkipsGlyph(font.Gdef,
                        buffer.GlyphsMutable[i], (GlyphClass)buffer.ClassesMutable[i],
                        lookup.MarkFilteringSet))
                {
                    i++;
                    continue;
                }

                var applied = false;
                foreach (var subtable in lookup.Subtables)
                {
                    if (TryApplySubtable(lookup, subtable, font, buffer, ref i, isSubstitution))
                    {
                        applied = true;
                        break;
                    }
                }
                if (!applied) i++;
            }
        }
    }

    /// <summary>
    /// Dispatch one subtable at buffer position <paramref name="i"/>. Returns true when
    /// it applied (having advanced <paramref name="i"/> past the applied output).
    /// H0: no lookup types implemented — always false. H1+: GSUB 1 (single),
    /// 4 (ligature); GPOS 1 (single), 2 (pair); then marks, contextual, cursive.
    /// </summary>
    private static bool TryApplySubtable(Lookup lookup, ReadOnlyMemory<byte> subtable,
        ShapingFont font, ShapeBuffer buffer, ref int i, bool isSubstitution)
    {
        _ = lookup;
        _ = subtable;
        _ = font;
        _ = buffer;
        _ = i;
        _ = isSubstitution;
        return false;
    }
}

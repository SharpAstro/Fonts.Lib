using System.Globalization;
using System.Text;
using SharpAstro.Fonts.IO;
using SharpAstro.Fonts.Shaping.Otl;

namespace SharpAstro.Fonts.Shaping;

/// <summary>
/// The shaping entry point: canonically orders combining marks, maps a
/// <see cref="ShapeBuffer"/>'s codepoints to glyphs, runs the font's GSUB/GPOS lookups
/// per the resolved <see cref="ShapePlan"/>, and finishes mark positioning.
///
/// <para><b>H3 status:</b> the full non-variable lookup set is applied — GSUB 1 (single),
/// 2 (multiple), 3 (alternate), 4 (ligature), 5/6 (context/chained context), 8 (reverse
/// chaining) and GPOS 1 (single), 2 (pair), 3 (cursive), 4/5/6 (mark attachment), 7/8
/// (context/chained context). Contextual lookups invoke nested lookups at matched
/// positions via <see cref="LookupRunner"/>. There is no normalization pass: the engine
/// assumes NFC input and reorders marks by canonical combining class, but never
/// composes/decomposes.</para>
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

        // Canonical mark ordering + grapheme-cluster merging run on codepoints, before cmap
        // mapping. Reordering is the only "normalization" the engine does (it never
        // composes/decomposes); merging gives each combining mark its base's cluster
        // (HarfBuzz cluster level 0 — the model DIR.Lib's caret mapping already assumes).
        CanonicalReorderMarks(buffer);
        MergeGraphemeClusters(buffer);

        MapToGlyphs(font, buffer);

        // Lookups run in LOGICAL order (spec); the buffer is reversed to visual order
        // only after positioning, so GSUB sequence matching, GPOS pairs, and mark
        // attachment see glyphs in reading order regardless of direction.
        var plan = font.GetPlan(script, buffer.Direction);
        if (font.Gsub is not null)
            new LookupRunner(font, font.Gsub, isSubstitution: true).Run(plan.SubstitutionLookups, buffer);
        if (font.Gpos is not null)
            new LookupRunner(font, font.Gpos, isSubstitution: false).Run(plan.PositioningLookups, buffer);

        // Turn mark attachments into on-line offsets and zero mark advances (still logical order).
        GposApplier.Finish(font, buffer);

        if (buffer.Direction == ShapeDirection.RightToLeft)
            buffer.Reverse();
    }

    /// <summary>
    /// Reorder each maximal run of combining marks (CCC &gt; 0) into ascending
    /// canonical-combining-class order (Unicode's canonical ordering algorithm), so
    /// below/above marks land in the deterministic order GPOS and HarfBuzz expect.
    /// A stable adjacent-swap sort preserves the typed order of equal-CCC marks; runs
    /// are delimited by starters (CCC 0), which never move. Operates on the codepoints
    /// still in the buffer (before <see cref="MapToGlyphs"/>).
    /// </summary>
    private static void CanonicalReorderMarks(ShapeBuffer buffer)
    {
        var cps = buffer.GlyphsMutable; // codepoints at this point
        var n = cps.Length;
        var i = 0;
        while (i < n)
        {
            if (CanonicalCombiningClass.Get(cps[i]) == 0) { i++; continue; }
            var runEnd = i + 1;
            while (runEnd < n && CanonicalCombiningClass.Get(cps[runEnd]) != 0) runEnd++;
            for (var a = i + 1; a < runEnd; a++)
                for (var b = a; b > i && CanonicalCombiningClass.Get(cps[b - 1]) > CanonicalCombiningClass.Get(cps[b]); b--)
                    buffer.SwapSlots(b - 1, b);
            i = runEnd;
        }
    }

    /// <summary>Codepoints → glyph ids via cmap, and glyph classes for lookupFlag skipping /
    /// mark positioning. When the font has no GDEF glyph classes, the class is synthesized
    /// from the Unicode general category (marks → Mark, everything else → Base).</summary>
    private static void MapToGlyphs(ShapingFont font, ShapeBuffer buffer)
    {
        var glyphs = buffer.GlyphsMutable;
        var classes = buffer.ClassesMutable;
        var synthesize = !font.Gdef.HasGlyphClasses;
        for (var i = 0; i < glyphs.Length; i++)
        {
            var codepoint = glyphs[i];
            var gid = font.Font.GetGlyphId(codepoint);
            glyphs[i] = gid;
            var cls = synthesize
                ? (IsUnicodeMark(codepoint) ? GlyphClass.Mark : GlyphClass.Base)
                : font.Gdef.GetGlyphClass(gid);
            classes[i] = (byte)cls;
        }
    }

    /// <summary>
    /// Merge each combining mark's cluster into the preceding glyph's (HarfBuzz cluster
    /// level 0): a base and the marks that follow it form one grapheme and share the
    /// base's cluster, so caret/hit-testing treats "q + combining acute" as a single
    /// editing unit. Consecutive marks chain the value leftward to the base. Operates on
    /// codepoints (before <see cref="MapToGlyphs"/>); a leading mark keeps its own cluster.
    /// </summary>
    private static void MergeGraphemeClusters(ShapeBuffer buffer)
    {
        var cps = buffer.GlyphsMutable; // codepoints at this point
        var clusters = buffer.ClustersMutable;
        for (var i = 1; i < cps.Length; i++)
            if (IsUnicodeMark(cps[i]))
                clusters[i] = clusters[i - 1];
    }

    private static bool IsUnicodeMark(uint codepoint)
        => Rune.TryCreate(codepoint, out var rune)
           && Rune.GetUnicodeCategory(rune) is
              UnicodeCategory.NonSpacingMark or
              UnicodeCategory.SpacingCombiningMark or
              UnicodeCategory.EnclosingMark;
}

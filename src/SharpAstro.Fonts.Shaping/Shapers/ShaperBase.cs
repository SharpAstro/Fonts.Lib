using System.Globalization;
using System.Text;
using SharpAstro.Fonts.IO;
using SharpAstro.Fonts.Shaping.Otl;
using SharpAstro.Fonts.Shaping.Ucd;

namespace SharpAstro.Fonts.Shaping;

/// <summary>
/// Base for the per-script shapers. Owns the shared pipeline — bracket mirroring and reversal
/// for RTL, canonical mark reordering, grapheme-cluster merging, cmap mapping, GSUB/GPOS
/// execution per the resolved <see cref="ShapePlan"/>, and the positioning-finish pass.
/// Subclasses contribute the feature set and any per-glyph mask assignment
/// (<see cref="ArabicShaper"/> for cursive joining; <see cref="DefaultShaper"/> adds nothing).
///
/// <para>There is no normalization pass: the engine assumes NFC input, reorders marks by
/// canonical combining class, but never composes/decomposes. Shapers are stateless singletons,
/// safe to share across threads.</para>
/// </summary>
internal abstract class ShaperBase
{
    /// <summary>GSUB features this shaper enables, in mask-bit-allocation order.</summary>
    internal abstract Tag[] GsubFeatures { get; }

    /// <summary>GPOS features this shaper enables.</summary>
    internal abstract Tag[] GposFeatures { get; }

    /// <summary>GSUB features applied per-glyph (a distinct mask bit each) rather than to the
    /// whole run. Empty by default; the Arabic positional forms for <see cref="ArabicShaper"/>.</summary>
    internal virtual Tag[] PerGlyphFeatures => [];

    /// <summary>Shape one single-script, single-direction run in place (see <see cref="Shaper.Shape"/>).</summary>
    public void Shape(ShapingFont font, ShapeBuffer buffer, Tag script)
    {
        var plan = font.GetPlan(script, buffer.Direction);

        // Bracket mirroring is a codepoint remap on RTL runs, before cmap (HarfBuzz's mirror pass).
        if (buffer.Direction == ShapeDirection.RightToLeft)
            MirrorCodepoints(buffer);

        // Canonical mark ordering + grapheme-cluster merging run on codepoints, before cmap.
        // Reordering is the only "normalization" the engine does; merging gives each combining
        // mark its base's cluster (HarfBuzz cluster level 0 — the model DIR.Lib's caret assumes).
        CanonicalReorderMarks(buffer);
        MergeGraphemeClusters(buffer);

        // Per-glyph feature masks (Arabic joining) are computed on codepoints, before mapping.
        AssignMasks(font, buffer, plan);

        MapToGlyphs(font, buffer);

        // Lookups run in LOGICAL order (spec); the buffer is reversed to visual order only after
        // positioning, so GSUB matching, GPOS pairs, and mark attachment see reading order.
        if (font.Gsub is not null)
            new LookupRunner(font, font.Gsub, isSubstitution: true).Run(plan.SubstitutionLookups, buffer);
        if (font.Gpos is not null)
            new LookupRunner(font, font.Gpos, isSubstitution: false).Run(plan.PositioningLookups, buffer);

        // Turn mark attachments into on-line offsets and zero mark advances (still logical order).
        GposApplier.Finish(font, buffer);

        if (buffer.Direction == ShapeDirection.RightToLeft)
            buffer.Reverse();
    }

    /// <summary>Assign per-glyph feature masks (default: none). Runs on codepoints, before cmap.</summary>
    protected virtual void AssignMasks(ShapingFont font, ShapeBuffer buffer, ShapePlan plan) { }

    /// <summary>Remap each codepoint to its Bidi_Mirroring_Glyph (parentheses, brackets, …) so an
    /// RTL run draws mirrored bracket pairs. A no-op for the vast majority of codepoints.</summary>
    private static void MirrorCodepoints(ShapeBuffer buffer)
    {
        var cps = buffer.GlyphsMutable; // codepoints at this point
        for (var i = 0; i < cps.Length; i++)
            cps[i] = BidiMirroring.Get(cps[i]);
    }

    /// <summary>
    /// Reorder each maximal run of combining marks (CCC &gt; 0) into ascending
    /// canonical-combining-class order (Unicode's canonical ordering algorithm), so below/above
    /// marks land in the deterministic order GPOS and HarfBuzz expect. A stable adjacent-swap
    /// sort preserves the typed order of equal-CCC marks; runs are delimited by starters (CCC 0),
    /// which never move. Operates on the codepoints still in the buffer (before mapping).
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
    /// mark positioning. When the font has no GDEF glyph classes, the class is synthesized from
    /// the Unicode general category (marks → Mark, everything else → Base).</summary>
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
    /// Merge each combining mark's cluster into the preceding glyph's (HarfBuzz cluster level 0):
    /// a base and the marks that follow it form one grapheme and share the base's cluster, so
    /// caret/hit-testing treats "q + combining acute" as a single editing unit. Consecutive marks
    /// chain the value leftward to the base. Operates on codepoints; a leading mark keeps its own
    /// cluster.
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

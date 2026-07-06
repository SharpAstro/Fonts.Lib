using SharpAstro.Fonts.IO;
using SharpAstro.Fonts.Shaping.Otl;

namespace SharpAstro.Fonts.Shaping;

/// <summary>
/// The lookups a shaping pass runs, resolved once per (script, direction) and cached by
/// <see cref="ShapingFont"/>. Substitution (GSUB) and positioning (GPOS) halves each hold
/// (lookup index, feature mask) pairs sorted by lookup index — the spec's application order
/// across a feature set. A lookup referenced by several enabled features carries the OR of
/// their masks; a glyph participates when <c>glyphMask &amp; lookupMask ≠ 0</c>.
///
/// <para>Most features apply to every glyph and share the always-on bit 0, so their lookups
/// run over the whole run. Only <em>per-glyph</em> features — the Arabic positional forms —
/// get distinct mask bits, which a <see cref="ShaperBase"/> sets on exactly the glyphs each
/// form applies to (see <see cref="GsubFeatureMask"/>). GPOS has no per-glyph features, so its
/// lookups all use bit 0 and can never collide with GSUB on the single per-glyph mask array.</para>
/// </summary>
public sealed class ShapePlan
{
    internal readonly record struct PlannedLookup(ushort LookupIndex, ushort Mask);

    internal PlannedLookup[] SubstitutionLookups { get; }
    internal PlannedLookup[] PositioningLookups { get; }

    // GSUB feature tag → the per-glyph mask bit it was assigned. Per-glyph features get a
    // distinct bit; every run-wide feature maps to the always-on bit 0 (value 1).
    private readonly Dictionary<Tag, ushort> _gsubFeatureBits;

    private ShapePlan(PlannedLookup[] substitution, PlannedLookup[] positioning, Dictionary<Tag, ushort> gsubFeatureBits)
    {
        SubstitutionLookups = substitution;
        PositioningLookups = positioning;
        _gsubFeatureBits = gsubFeatureBits;
    }

    /// <summary>The per-glyph mask bit assigned to a GSUB <paramref name="feature"/>, or 0 if it
    /// isn't in this plan. Per-glyph features (Arabic <c>isol/init/medi/fina</c>) get distinct
    /// bits; run-wide features share bit 0 and report 1.</summary>
    internal ushort GsubFeatureMask(Tag feature) => _gsubFeatureBits.GetValueOrDefault(feature);

    /// <summary>Sentinel under which a LangSys's required feature is collected — always enabled.</summary>
    private static readonly Tag RequiredFeatureTag = new(0);

    internal static ShapePlan Build(LayoutTable? gsub, LayoutTable? gpos, Tag script, ShapeDirection direction, ShaperBase shaper)
    {
        _ = direction; // the shaper (chosen by script) fixes the feature set; direction only drives mirroring/reversal
        var gsubFeatureBits = new Dictionary<Tag, ushort>();
        var substitution = Resolve(gsub, script, shaper.GsubFeatures, shaper.PerGlyphFeatures, gsubFeatureBits);
        var positioning = Resolve(gpos, script, shaper.GposFeatures, perGlyphFeatures: [], featureBitsOut: null);
        return new ShapePlan(substitution, positioning, gsubFeatureBits);
    }

    private static PlannedLookup[] Resolve(LayoutTable? table, Tag script, Tag[] wantedFeatures,
        Tag[] perGlyphFeatures, Dictionary<Tag, ushort>? featureBitsOut)
    {
        if (table is null) return [];

        // Feature tag → mask bit. Per-glyph features each claim a distinct bit (1..15) so a
        // shaper can enable exactly one per glyph; every other feature — and any overflow past
        // 15 — shares the always-on bit 0.
        var featureBits = new Dictionary<Tag, ushort>(wantedFeatures.Length);
        var nextBit = 1;
        foreach (var tag in wantedFeatures)
        {
            var perGlyph = Array.IndexOf(perGlyphFeatures, tag) >= 0;
            featureBits[tag] = perGlyph && nextBit <= 15 ? (ushort)(1 << nextBit++) : (ushort)1;
        }
        if (featureBitsOut is not null)
        {
            featureBitsOut.EnsureCapacity(featureBits.Count);
            foreach (var (tag, bit) in featureBits) featureBitsOut[tag] = bit;
        }

        // lookup index → accumulated mask across the features that reference it.
        var lookupMasks = new Dictionary<ushort, ushort>();
        var found = table.TryCollectFeatures(script, RequiredFeatureTag, (tag, lookupIndices) =>
        {
            ushort mask;
            if (tag == RequiredFeatureTag) mask = 1;
            else if (!featureBits.TryGetValue(tag, out mask)) return; // feature not in this plan's set

            foreach (var idx in lookupIndices)
            {
                if (idx >= table.Lookups.Length) continue;
                lookupMasks[idx] = (ushort)(lookupMasks.GetValueOrDefault(idx) | mask);
            }
        });
        if (!found || lookupMasks.Count == 0) return [];

        var planned = new PlannedLookup[lookupMasks.Count];
        var i = 0;
        foreach (var (idx, mask) in lookupMasks) planned[i++] = new PlannedLookup(idx, mask);
        Array.Sort(planned, static (a, b) => a.LookupIndex.CompareTo(b.LookupIndex));
        return planned;
    }
}

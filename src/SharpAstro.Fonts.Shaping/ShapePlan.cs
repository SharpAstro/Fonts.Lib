using SharpAstro.Fonts.IO;
using SharpAstro.Fonts.Shaping.Otl;

namespace SharpAstro.Fonts.Shaping;

/// <summary>
/// The lookups a shaping pass runs, resolved once per (script, direction) and cached
/// by <see cref="ShapingFont"/>. Substitution (GSUB) and positioning (GPOS) halves
/// each hold (lookup index, feature mask) pairs sorted by lookup index — the spec's
/// application order across a feature set. A lookup referenced by several enabled
/// features carries the OR of their masks; a glyph participates when
/// <c>glyphMask &amp; lookupMask ≠ 0</c>. Until a shaper assigns per-glyph masks
/// (Arabic, H4), every glyph's mask is all-ones, so masks are inert.
/// </summary>
public sealed class ShapePlan
{
    internal readonly record struct PlannedLookup(ushort LookupIndex, ushort Mask);

    internal PlannedLookup[] SubstitutionLookups { get; }
    internal PlannedLookup[] PositioningLookups { get; }

    private ShapePlan(PlannedLookup[] substitution, PlannedLookup[] positioning)
    {
        SubstitutionLookups = substitution;
        PositioningLookups = positioning;
    }

    // H0/H1 default feature sets (plan doc): horizontal text, script-independent.
    // rlig is only meaningful once the Arabic shaper assigns joining masks but is
    // harmless to enable generally (HB enables it by default too).
    private static readonly Tag[] DefaultGsubFeatures =
        [new("ccmp"), new("liga"), new("clig"), new("calt"), new("rlig")];
    private static readonly Tag[] DefaultGposFeatures =
        [new("kern"), new("mark"), new("mkmk")];

    /// <summary>Sentinel under which a LangSys's required feature is collected — always enabled.</summary>
    private static readonly Tag RequiredFeatureTag = new(0);

    internal static ShapePlan Build(LayoutTable? gsub, LayoutTable? gpos, Tag script, ShapeDirection direction)
    {
        _ = direction; // direction-specific feature sets arrive with the H4 shapers
        return new ShapePlan(
            Resolve(gsub, script, DefaultGsubFeatures),
            Resolve(gpos, script, DefaultGposFeatures));
    }

    private static PlannedLookup[] Resolve(LayoutTable? table, Tag script, Tag[] wantedFeatures)
    {
        if (table is null) return [];

        // Feature tag → mask bit. Bit 0 is the always-on bit (required feature);
        // distinct wanted features get bits 1..15 and overflow shares bit 0.
        var featureBits = new Dictionary<Tag, ushort>(wantedFeatures.Length);
        var nextBit = 1;
        foreach (var tag in wantedFeatures)
        {
            featureBits[tag] = nextBit <= 15 ? (ushort)(1 << nextBit++) : (ushort)1;
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

using SharpAstro.Fonts.IO;
using SharpAstro.Fonts.Variations;
using SharpAstro.Fonts.Tables.Hvar;

namespace SharpAstro.Fonts.Tables.Vvar;

/// <summary>
/// Vertical Metrics Variations table ('VVAR'). Mirrors the layout of HVAR
/// but applies deltas to vertical advance heights (and optionally TSB/BSB/vOrg)
/// rather than horizontal advance widths.
///
/// <para>Spec: https://learn.microsoft.com/typography/opentype/spec/vvar</para>
/// </summary>
internal sealed class VvarTable
{
    private readonly ItemVariationStore _ivs;

    /// <summary>Maps glyphId → (outer, inner) for advance-height deltas.
    /// Null = identity mapping (outer=0, inner=glyphId).</summary>
    private readonly DeltaSetIndexMap? _advanceHeightMap;

    private VvarTable(ItemVariationStore ivs, DeltaSetIndexMap? advanceHeightMap)
    {
        _ivs = ivs;
        _advanceHeightMap = advanceHeightMap;
    }

    /// <summary>The underlying Item Variation Store.</summary>
    public ItemVariationStore VariationStore => _ivs;

    /// <summary>
    /// Get the advance-height delta for <paramref name="glyphId"/>
    /// at the given normalized axis coordinates. Returns 0 when no variation
    /// data is present or the glyph is out of range.
    /// </summary>
    public float GetAdvanceHeightDelta(uint glyphId, ReadOnlySpan<float> normalizedCoords)
    {
        int outer, inner;
        if (_advanceHeightMap is not null)
            (outer, inner) = _advanceHeightMap.Map(glyphId);
        else
            (outer, inner) = (0, (int)glyphId);
        return _ivs.GetDelta(outer, inner, normalizedCoords);
    }

    public static VvarTable Parse(ReadOnlySpan<byte> data)
    {
        var r = new BigEndianReader(data);
        var majorVersion = r.ReadUInt16(); // must be 1
        var minorVersion = r.ReadUInt16(); // must be 0
        var ivsOffset = r.ReadUInt32();
        var advHeightMapOffset = r.ReadUInt32();
        // tsbMappingOffset, bsbMappingOffset, vOrgMappingOffset follow but are
        // not required for advance-height delta lookup — skip for now.

        var ivs = ItemVariationStore.Parse(data[(int)ivsOffset..]);

        DeltaSetIndexMap? advMap = null;
        if (advHeightMapOffset != 0)
            advMap = DeltaSetIndexMap.Parse(data[(int)advHeightMapOffset..]);

        return new VvarTable(ivs, advMap);
    }
}

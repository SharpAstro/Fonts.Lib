using SharpAstro.Fonts.IO;
using SharpAstro.Fonts.Variations;

namespace SharpAstro.Fonts.Tables.Mvar;

/// <summary>
/// Metrics Variations table ('MVAR'). Maps 4-byte metric tags to
/// (outerIndex, innerIndex) pairs in an <see cref="ItemVariationStore"/>,
/// enabling per-axis deltas for global font metrics such as OS/2
/// ascender/descender, x-height, cap-height, underline values, etc.
///
/// <para>Spec: https://docs.microsoft.com/typography/opentype/spec/mvar</para>
///
/// <para>Common tags:
/// <list type="bullet">
/// <item><term>hasc</term><description>OS/2 sTypoAscender</description></item>
/// <item><term>hdsc</term><description>OS/2 sTypoDescender</description></item>
/// <item><term>hlgp</term><description>OS/2 sTypoLineGap</description></item>
/// <item><term>hcla</term><description>OS/2 usWinAscent</description></item>
/// <item><term>hcld</term><description>OS/2 usWinDescent</description></item>
/// <item><term>vasc</term><description>OS/2 sxHeight (vert ascender role)</description></item>
/// <item><term>vdsc</term><description>Vertical descender</description></item>
/// <item><term>vlgp</term><description>Vertical line gap</description></item>
/// <item><term>xhgt</term><description>OS/2 sxHeight</description></item>
/// <item><term>cpht</term><description>OS/2 sCapHeight</description></item>
/// <item><term>sbxs</term><description>Subscript x-size</description></item>
/// <item><term>sbys</term><description>Subscript y-size</description></item>
/// <item><term>sbxo</term><description>Subscript x-offset</description></item>
/// <item><term>sbyo</term><description>Subscript y-offset</description></item>
/// </list>
/// </para>
/// </summary>
internal sealed class MvarTable
{
    private readonly ItemVariationStore? _ivs;
    // Maps Tag.Value → packed (outer << 16) | inner for O(1) lookup.
    private readonly Dictionary<uint, uint> _tagMap;

    private MvarTable(ItemVariationStore? ivs, Dictionary<uint, uint> tagMap)
    {
        _ivs = ivs;
        _tagMap = tagMap;
    }

    /// <summary>
    /// Return the variation delta (in FUnits, as a float) for the metric
    /// identified by <paramref name="metricTag"/> at the given normalized axis
    /// coordinates. Returns 0 if the tag is not present in this table or there
    /// is no Item Variation Store.
    /// </summary>
    public float GetDelta(Tag metricTag, ReadOnlySpan<float> normalizedCoords)
    {
        if (_ivs is null) return 0f;
        if (!_tagMap.TryGetValue(metricTag.Value, out var packed))
            return 0f;
        var outer = (int)(packed >> 16);
        var inner = (int)(packed & 0xFFFF);
        return _ivs.GetDelta(outer, inner, normalizedCoords);
    }

    /// <summary>
    /// Parse an MVAR table from <paramref name="data"/> (the slice starting at
    /// the table's own offset within the font file).
    /// </summary>
    public static MvarTable Parse(ReadOnlySpan<byte> data)
    {
        var r = new BigEndianReader(data);
        var majorVersion = r.ReadUInt16();    // must be 1
        var minorVersion = r.ReadUInt16();    // must be 0
        r.ReadUInt16();                        // reserved, must be 0 — skip
        var valueRecordSize = r.ReadUInt16(); // must be 8
        var valueRecordCount = r.ReadUInt16();
        // Offset16 (2 bytes) per OpenType spec — relative to start of MVAR table.
        var ivsOffset = (uint)r.ReadUInt16();

        // Read ValueRecord entries: Tag(4) + uint16 outer + uint16 inner
        var tagMap = new Dictionary<uint, uint>(valueRecordCount);
        for (var i = 0; i < valueRecordCount; i++)
        {
            var tagValue = r.ReadUInt32();
            var outer = r.ReadUInt16();
            var inner = r.ReadUInt16();
            tagMap[tagValue] = ((uint)outer << 16) | inner;
        }

        // ivsOffset of 0 means no IVS is present (valueRecordCount is 0).
        if (ivsOffset == 0 || tagMap.Count == 0)
            return new MvarTable(null, tagMap);

        var ivs = ItemVariationStore.Parse(data[(int)ivsOffset..]);

        return new MvarTable(ivs, tagMap);
    }
}

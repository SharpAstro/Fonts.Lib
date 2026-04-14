using SharpAstro.Fonts.IO;

namespace SharpAstro.Fonts.Tables.Vmtx;

/// <summary>
/// Parsed 'vmtx' (vertical metrics) table.
/// Spec: https://learn.microsoft.com/typography/opentype/spec/vmtx
///
/// Layout: <c>numberOfVMetrics</c> long-metric entries (advanceHeight + tsb)
/// followed by trailing TSB-only entries that share the last advance height.
/// </summary>
public sealed class VmtxTable
{
    private readonly ushort[] _advanceHeights;
    private readonly short[] _topSideBearings;

    private VmtxTable(ushort[] advanceHeights, short[] topSideBearings)
    {
        _advanceHeights = advanceHeights;
        _topSideBearings = topSideBearings;
    }

    public ushort GetAdvanceHeight(uint glyphId)
        => _advanceHeights[Math.Min(glyphId, (uint)_advanceHeights.Length - 1)];

    public short GetTopSideBearing(uint glyphId)
        => _topSideBearings[Math.Min(glyphId, (uint)_topSideBearings.Length - 1)];

    public static VmtxTable Parse(ReadOnlySpan<byte> data, ushort numberOfVMetrics, ushort numGlyphs)
    {
        if (numberOfVMetrics == 0)
            throw new InvalidDataException("vmtx: numberOfVMetrics must be > 0.");

        var r = new BigEndianReader(data);
        var adv = new ushort[numGlyphs];
        var tsb = new short[numGlyphs];

        ushort lastAdv = 0;
        for (uint i = 0; i < numberOfVMetrics; i++)
        {
            lastAdv = r.ReadUInt16();
            adv[i] = lastAdv;
            tsb[i] = r.ReadInt16();
        }
        // Trailing TSB-only entries share lastAdv.
        for (uint i = numberOfVMetrics; i < numGlyphs; i++)
        {
            adv[i] = lastAdv;
            // Some fonts truncate the trailing TSB array; guard.
            tsb[i] = r.Remaining >= 2 ? r.ReadInt16() : (short)0;
        }
        return new VmtxTable(adv, tsb);
    }
}

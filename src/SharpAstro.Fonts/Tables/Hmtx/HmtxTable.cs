using SharpAstro.Fonts.IO;

namespace SharpAstro.Fonts.Tables.Hmtx;

/// <summary>
/// Parsed 'hmtx' (horizontal metrics) table.
/// Spec: https://learn.microsoft.com/typography/opentype/spec/hmtx
///
/// Layout: <c>numberOfHMetrics</c> long-metric entries (advanceWidth + lsb)
/// followed by trailing LSB-only entries that share the last advance width.
/// </summary>
public sealed class HmtxTable
{
    private readonly ushort[] _advanceWidths;
    private readonly short[] _leftSideBearings;

    private HmtxTable(ushort[] advanceWidths, short[] leftSideBearings)
    {
        _advanceWidths = advanceWidths;
        _leftSideBearings = leftSideBearings;
    }

    public ushort GetAdvanceWidth(uint glyphId)
        => _advanceWidths[Math.Min(glyphId, (uint)_advanceWidths.Length - 1)];

    public short GetLeftSideBearing(uint glyphId)
        => _leftSideBearings[Math.Min(glyphId, (uint)_leftSideBearings.Length - 1)];

    public static HmtxTable Parse(ReadOnlySpan<byte> data, ushort numberOfHMetrics, ushort numGlyphs)
    {
        if (numberOfHMetrics == 0)
            throw new InvalidDataException("hmtx: numberOfHMetrics must be > 0.");

        var r = new BigEndianReader(data);
        var adv = new ushort[numGlyphs];
        var lsb = new short[numGlyphs];

        ushort lastAdv = 0;
        for (uint i = 0; i < numberOfHMetrics; i++)
        {
            lastAdv = r.ReadUInt16();
            adv[i] = lastAdv;
            lsb[i] = r.ReadInt16();
        }
        // Trailing LSB-only entries share lastAdv.
        for (uint i = numberOfHMetrics; i < numGlyphs; i++)
        {
            adv[i] = lastAdv;
            // Some fonts truncate the trailing LSB array; guard.
            lsb[i] = r.Remaining >= 2 ? r.ReadInt16() : (short)0;
        }
        return new HmtxTable(adv, lsb);
    }
}

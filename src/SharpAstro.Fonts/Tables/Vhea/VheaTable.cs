using SharpAstro.Fonts.IO;

namespace SharpAstro.Fonts.Tables.Vhea;

/// <summary>
/// Parsed 'vhea' (vertical header) table.
/// Spec: https://learn.microsoft.com/typography/opentype/spec/vhea
/// </summary>
public sealed class VheaTable
{
    public short Ascender { get; }
    public short Descender { get; }
    public short LineGap { get; }
    public ushort AdvanceHeightMax { get; }
    /// <summary>Number of advance-height entries in 'vmtx'. Tail of 'vmtx' is TSB-only.</summary>
    public ushort NumberOfVMetrics { get; }

    private VheaTable(short asc, short desc, short lineGap, ushort advMax, ushort numV)
    {
        Ascender = asc;
        Descender = desc;
        LineGap = lineGap;
        AdvanceHeightMax = advMax;
        NumberOfVMetrics = numV;
    }

    public static VheaTable Parse(ReadOnlySpan<byte> data)
    {
        var r = new BigEndianReader(data);
        // version (uint32 or fixed16.16)
        r.Skip(4);
        var asc = r.ReadInt16();
        var desc = r.ReadInt16();
        var lineGap = r.ReadInt16();
        var advMax = r.ReadUInt16();
        // minTopSideBearing(int16) + minBottomSideBearing(int16) + yMaxExtent(int16)
        r.Skip(6);
        // caretSlopeRise(int16) + caretSlopeRun(int16) + caretOffset(int16)
        r.Skip(6);
        // 4 reserved int16 = 8 bytes
        r.Skip(8);
        // metricDataFormat(int16)
        r.Skip(2);
        var numV = r.ReadUInt16();
        return new VheaTable(asc, desc, lineGap, advMax, numV);
    }
}

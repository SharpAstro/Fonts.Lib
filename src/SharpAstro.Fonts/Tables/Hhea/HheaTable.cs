using SharpAstro.Fonts.IO;

namespace SharpAstro.Fonts.Tables.Hhea;

/// <summary>
/// Parsed 'hhea' (horizontal header) table.
/// Spec: https://learn.microsoft.com/typography/opentype/spec/hhea
/// </summary>
public sealed class HheaTable
{
    public short Ascender { get; }
    public short Descender { get; }
    public short LineGap { get; }
    public ushort AdvanceWidthMax { get; }
    /// <summary>Number of advance-width entries in 'hmtx'. Tail of 'hmtx' is LSB-only.</summary>
    public ushort NumberOfHMetrics { get; }

    private HheaTable(short asc, short desc, short lineGap, ushort advMax, ushort numH)
    {
        Ascender = asc;
        Descender = desc;
        LineGap = lineGap;
        AdvanceWidthMax = advMax;
        NumberOfHMetrics = numH;
    }

    public static HheaTable Parse(ReadOnlySpan<byte> data)
    {
        var r = new BigEndianReader(data);
        // majorVersion + minorVersion (uint16 + uint16)
        r.Skip(4);
        var asc = r.ReadInt16();
        var desc = r.ReadInt16();
        var lineGap = r.ReadInt16();
        var advMax = r.ReadUInt16();
        // minLeftSideBearing(int16) + minRightSideBearing(int16) + xMaxExtent(int16)
        r.Skip(6);
        // caretSlopeRise(int16) + caretSlopeRun(int16) + caretOffset(int16)
        r.Skip(6);
        // 4 reserved int16 = 8 bytes
        r.Skip(8);
        // metricDataFormat(int16)
        r.Skip(2);
        var numH = r.ReadUInt16();
        return new HheaTable(asc, desc, lineGap, advMax, numH);
    }
}

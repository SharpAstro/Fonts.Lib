using SharpAstro.Fonts.IO;

namespace SharpAstro.Fonts.Tables.Head;

/// <summary>
/// Parsed 'head' table.
/// Spec: https://learn.microsoft.com/typography/opentype/spec/head
/// </summary>
public sealed class HeadTable
{
    public ushort UnitsPerEm { get; }
    public short XMin { get; }
    public short YMin { get; }
    public short XMax { get; }
    public short YMax { get; }
    public ushort MacStyle { get; }
    /// <summary>0 = short offsets in 'loca', 1 = long offsets.</summary>
    public short IndexToLocFormat { get; }

    private HeadTable(ushort upem, short xMin, short yMin, short xMax, short yMax,
        ushort macStyle, short indexToLocFormat)
    {
        UnitsPerEm = upem;
        XMin = xMin;
        YMin = yMin;
        XMax = xMax;
        YMax = yMax;
        MacStyle = macStyle;
        IndexToLocFormat = indexToLocFormat;
    }

    /// <summary>
    /// Synthesize a 'head' for a bare CFF program (CIDFontType0 /FontFile3), which
    /// ships no SFNT tables. Units-per-em and the bounding box come from the CFF
    /// FontMatrix / FontBBox; IndexToLocFormat is irrelevant (no 'loca'), MacStyle 0.
    /// </summary>
    internal static HeadTable ForCff(ushort unitsPerEm, short xMin, short yMin, short xMax, short yMax)
        => new(unitsPerEm, xMin, yMin, xMax, yMax, macStyle: 0, indexToLocFormat: 0);

    public static HeadTable Parse(ReadOnlySpan<byte> data)
    {
        var r = new BigEndianReader(data);
        // majorVersion(uint16) + minorVersion(uint16)
        r.Skip(4);
        // fontRevision(Fixed32) + checksumAdjustment(uint32) + magicNumber(uint32)
        r.Skip(12);
        // flags(uint16)
        r.Skip(2);
        var upem = r.ReadUInt16();
        // created(LONGDATETIME=int64) + modified(LONGDATETIME=int64)
        r.Skip(16);
        var xMin = r.ReadInt16();
        var yMin = r.ReadInt16();
        var xMax = r.ReadInt16();
        var yMax = r.ReadInt16();
        var macStyle = r.ReadUInt16();
        // lowestRecPPEM(uint16) + fontDirectionHint(int16)
        r.Skip(4);
        var indexToLocFormat = r.ReadInt16();
        // glyphDataFormat(int16) — must be 0
        return new HeadTable(upem, xMin, yMin, xMax, yMax, macStyle, indexToLocFormat);
    }
}

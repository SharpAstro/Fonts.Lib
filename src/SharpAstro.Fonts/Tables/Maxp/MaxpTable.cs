using SharpAstro.Fonts.IO;

namespace SharpAstro.Fonts.Tables.Maxp;

/// <summary>
/// Parsed 'maxp' table. Both v0.5 (CFF, 6 bytes) and v1.0 (TT, 32 bytes) supported.
/// Spec: https://learn.microsoft.com/typography/opentype/spec/maxp
/// </summary>
public sealed class MaxpTable
{
    public uint Version { get; }
    public ushort NumGlyphs { get; }

    private MaxpTable(uint version, ushort numGlyphs)
    {
        Version = version;
        NumGlyphs = numGlyphs;
    }

    public static MaxpTable Parse(ReadOnlySpan<byte> data)
    {
        var r = new BigEndianReader(data);
        var version = r.ReadUInt32();
        var numGlyphs = r.ReadUInt16();
        // v1.0 has 13 more uint16 fields after numGlyphs that we don't need yet.
        return new MaxpTable(version, numGlyphs);
    }
}

using SharpAstro.Fonts.IO;

namespace SharpAstro.Fonts.Tables.Loca;

/// <summary>
/// Parsed 'loca' (index-to-location) table — offsets into 'glyf' for each glyph.
/// Spec: https://learn.microsoft.com/typography/opentype/spec/loca
///
/// Has <c>numGlyphs + 1</c> entries; the trailing sentinel gives the length
/// of the last glyph: <c>length(i) = offset(i+1) - offset(i)</c>. A length of
/// 0 means the glyph has no outline (e.g. space).
/// </summary>
public sealed class LocaTable
{
    private readonly uint[] _offsets;

    private LocaTable(uint[] offsets) => _offsets = offsets;

    public int Count => _offsets.Length - 1;

    public uint GetOffset(uint glyphId) => _offsets[glyphId];
    public uint GetLength(uint glyphId) => _offsets[glyphId + 1] - _offsets[glyphId];

    /// <summary>
    /// Parse loca. <paramref name="indexToLocFormat"/> comes from 'head':
    /// 0 → short offsets (uint16, divided by 2); 1 → long offsets (uint32).
    /// </summary>
    public static LocaTable Parse(ReadOnlySpan<byte> data, short indexToLocFormat, ushort numGlyphs)
    {
        var r = new BigEndianReader(data);
        var n = numGlyphs + 1;
        var offsets = new uint[n];
        if (indexToLocFormat == 0)
        {
            for (var i = 0; i < n; i++)
                offsets[i] = (uint)r.ReadUInt16() * 2u;
        }
        else
        {
            for (var i = 0; i < n; i++)
                offsets[i] = r.ReadUInt32();
        }
        return new LocaTable(offsets);
    }
}

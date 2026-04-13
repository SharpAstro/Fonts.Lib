using SharpAstro.Fonts.IO;

namespace SharpAstro.Fonts.Tables.Cff;

/// <summary>
/// Maps a CFF GID → SID (string id) for non-CID fonts, or GID → CID for
/// CID-keyed fonts. We don't currently consume the SID values (charset is
/// only needed for naming / glyph-name lookup), but parsing it correctly
/// is required to know how many bytes the charset occupies.
///
/// <para>Spec: Adobe Tech Note #5176 §13. Three formats: 0 (array),
/// 1 (range with 1-byte length), 2 (range with 2-byte length).</para>
/// </summary>
internal sealed class CffCharset
{
    /// <summary>SID/CID value per GID. Index 0 is always .notdef = 0.</summary>
    public ushort[] GidToSid { get; }

    public CffCharset(ushort[] gidToSid) => GidToSid = gidToSid;

    public ushort GetSid(uint gid)
        => gid < (uint)GidToSid.Length ? GidToSid[gid] : (ushort)0;

    public static CffCharset Parse(ReadOnlySpan<byte> table, int offset, int numGlyphs)
    {
        var arr = new ushort[numGlyphs];
        // .notdef
        arr[0] = 0;
        if (numGlyphs <= 1) return new CffCharset(arr);

        var r = new BigEndianReader(table, offset);
        var format = r.ReadByte();
        switch (format)
        {
            case 0:
            {
                for (var i = 1; i < numGlyphs; i++)
                    arr[i] = r.ReadUInt16();
                break;
            }
            case 1:
            {
                var i = 1;
                while (i < numGlyphs)
                {
                    var first = r.ReadUInt16();
                    var nLeft = r.ReadByte();
                    for (var j = 0; j <= nLeft && i < numGlyphs; j++)
                        arr[i++] = (ushort)(first + j);
                }
                break;
            }
            case 2:
            {
                var i = 1;
                while (i < numGlyphs)
                {
                    var first = r.ReadUInt16();
                    var nLeft = r.ReadUInt16();
                    for (var j = 0; j <= nLeft && i < numGlyphs; j++)
                        arr[i++] = (ushort)(first + j);
                }
                break;
            }
            default:
                throw new InvalidDataException($"CFF charset: unknown format {format}");
        }
        return new CffCharset(arr);
    }
}

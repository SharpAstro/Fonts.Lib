using SharpAstro.Fonts.IO;

namespace SharpAstro.Fonts.Tables.Cff;

/// <summary>
/// FDSelect — for CID-keyed fonts, maps GID → font-dict index (which Private
/// DICT and local subroutines apply to that glyph).
///
/// <para>Spec: Adobe Tech Note #5176 §19. Formats 0 (array) and 3 (range).
/// Format 4 (CFF2) is uint32 ranges.</para>
/// </summary>
internal sealed class CffFdSelect
{
    private readonly byte[] _gidToFd;

    public CffFdSelect(byte[] gidToFd) => _gidToFd = gidToFd;

    public byte GetFdIndex(uint gid)
        => gid < (uint)_gidToFd.Length ? _gidToFd[gid] : (byte)0;

    public static CffFdSelect Parse(ReadOnlySpan<byte> table, int offset, int numGlyphs)
    {
        var r = new BigEndianReader(table, offset);
        var format = r.ReadByte();
        var arr = new byte[numGlyphs];
        switch (format)
        {
            case 0:
                for (var i = 0; i < numGlyphs; i++) arr[i] = r.ReadByte();
                break;
            case 3:
            {
                var nRanges = r.ReadUInt16();
                if (nRanges == 0) break;
                var first = (int)r.ReadUInt16();
                var fd = r.ReadByte();
                for (var k = 1; k <= nRanges; k++)
                {
                    int next;
                    byte nextFd;
                    if (k < nRanges)
                    {
                        next = r.ReadUInt16();
                        nextFd = r.ReadByte();
                    }
                    else
                    {
                        // sentinel
                        next = r.ReadUInt16();
                        nextFd = 0;
                    }
                    if (next > numGlyphs) next = numGlyphs;
                    for (var i = first; i < next; i++) arr[i] = fd;
                    first = next;
                    fd = nextFd;
                }
                break;
            }
            default:
                throw new InvalidDataException($"FDSelect: unsupported format {format}");
        }
        return new CffFdSelect(arr);
    }
}

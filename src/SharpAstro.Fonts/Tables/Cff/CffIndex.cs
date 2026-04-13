using SharpAstro.Fonts.IO;

namespace SharpAstro.Fonts.Tables.Cff;

/// <summary>
/// A CFF "INDEX" — variable-length array of variable-sized objects.
/// Spec: Adobe Tech Note #5176 §5 (CFF1) / OpenType CFF2 §6 (CFF2).
///
/// <para>Layout (CFF1): <c>count(uint16) offSize(uint8) offsets[count+1] data[]</c>.
/// CFF2 uses <c>count(uint32)</c>; otherwise identical. Offsets are 1-based
/// from the start of the data area.</para>
///
/// <para>Stored compactly as a <c>ReadOnlyMemory&lt;byte&gt;</c> view of the
/// underlying CFF table; per-object slices are produced on demand.</para>
/// </summary>
internal sealed class CffIndex
{
    private readonly ReadOnlyMemory<byte> _data;
    private readonly int[] _objectOffsets; // length = Count + 1

    /// <summary>Number of objects in the INDEX.</summary>
    public int Count { get; }

    /// <summary>Total size of the INDEX in bytes (header + offsets + data).</summary>
    public int TotalSize { get; }

    private CffIndex(ReadOnlyMemory<byte> data, int[] objectOffsets, int totalSize)
    {
        _data = data;
        _objectOffsets = objectOffsets;
        Count = objectOffsets.Length - 1;
        TotalSize = totalSize;
    }

    /// <summary>Empty INDEX (count == 0).</summary>
    public static readonly CffIndex Empty = new(ReadOnlyMemory<byte>.Empty, [0], 2);

    /// <summary>Get object i as a span over the underlying data.</summary>
    public ReadOnlySpan<byte> GetObject(int index)
    {
        var start = _objectOffsets[index];
        var end = _objectOffsets[index + 1];
        return _data.Span.Slice(start, end - start);
    }

    /// <summary>Get object i as a memory view (for handing off to recursive parsers).</summary>
    public ReadOnlyMemory<byte> GetObjectMemory(int index)
    {
        var start = _objectOffsets[index];
        var end = _objectOffsets[index + 1];
        return _data.Slice(start, end - start);
    }

    /// <summary>
    /// Parse an INDEX starting at <paramref name="offset"/> within
    /// <paramref name="table"/>. <paramref name="cff2"/> selects whether the
    /// count is a uint16 (CFF1) or uint32 (CFF2).
    /// </summary>
    public static CffIndex Parse(ReadOnlyMemory<byte> table, int offset, bool cff2 = false)
    {
        var span = table.Span;
        var r = new BigEndianReader(span, offset);
        var headerStart = offset;

        long count = cff2 ? r.ReadUInt32() : r.ReadUInt16();
        if (count == 0)
        {
            // CFF1: 2 bytes (count). CFF2: 4 bytes.
            return new CffIndex(ReadOnlyMemory<byte>.Empty, [0], cff2 ? 4 : 2);
        }

        var offSize = r.ReadByte();
        if (offSize is < 1 or > 4)
            throw new InvalidDataException($"CFF INDEX: invalid offSize {offSize}");

        var offsets = new int[count + 1];
        for (var i = 0; i <= count; i++)
            offsets[i] = (int)ReadOff(ref r, offSize) - 1; // convert 1-based to 0-based

        // Object data follows the offset array.
        var dataStart = r.Position;
        var dataLen = offsets[count];
        var totalSize = (dataStart + dataLen) - headerStart;
        var data = table.Slice(dataStart, dataLen);

        return new CffIndex(data, offsets, totalSize);
    }

    private static uint ReadOff(ref BigEndianReader r, byte offSize)
    {
        return offSize switch
        {
            1 => r.ReadByte(),
            2 => r.ReadUInt16(),
            3 => r.ReadUInt24(),
            4 => r.ReadUInt32(),
            _ => throw new InvalidDataException($"Invalid offSize {offSize}"),
        };
    }
}

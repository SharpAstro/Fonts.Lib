using System.Buffers.Binary;

namespace SharpAstro.Fonts.IO;

/// <summary>
/// Span-based big-endian reader. All OpenType / SFNT data is big-endian.
/// Cheap to copy by value; use <c>ref</c> at hot paths if you want
/// to mutate position in place.
/// </summary>
internal ref struct BigEndianReader
{
    private readonly ReadOnlySpan<byte> _data;
    private int _pos;

    public BigEndianReader(ReadOnlySpan<byte> data, int offset = 0)
    {
        _data = data;
        _pos = offset;
    }

    public int Position
    {
        readonly get => _pos;
        set => _pos = value;
    }

    public readonly int Length => _data.Length;
    public readonly int Remaining => _data.Length - _pos;
    public readonly ReadOnlySpan<byte> Data => _data;

    public void Skip(int count) => _pos += count;

    public byte ReadByte() => _data[_pos++];

    public sbyte ReadSByte() => (sbyte)_data[_pos++];

    public ushort ReadUInt16()
    {
        var v = BinaryPrimitives.ReadUInt16BigEndian(_data[_pos..]);
        _pos += 2;
        return v;
    }

    public short ReadInt16()
    {
        var v = BinaryPrimitives.ReadInt16BigEndian(_data[_pos..]);
        _pos += 2;
        return v;
    }

    public uint ReadUInt24()
    {
        uint v = ((uint)_data[_pos] << 16) | ((uint)_data[_pos + 1] << 8) | _data[_pos + 2];
        _pos += 3;
        return v;
    }

    public uint ReadUInt32()
    {
        var v = BinaryPrimitives.ReadUInt32BigEndian(_data[_pos..]);
        _pos += 4;
        return v;
    }

    public int ReadInt32()
    {
        var v = BinaryPrimitives.ReadInt32BigEndian(_data[_pos..]);
        _pos += 4;
        return v;
    }

    public long ReadInt64()
    {
        var v = BinaryPrimitives.ReadInt64BigEndian(_data[_pos..]);
        _pos += 8;
        return v;
    }

    /// <summary>16.16 fixed-point as float.</summary>
    public float ReadFixed1616() => ReadInt32() / 65536f;

    /// <summary>2.14 fixed-point (F2DOT14) as float.</summary>
    public float ReadF2Dot14() => ReadInt16() / 16384f;

    /// <summary>FWORD: 16-bit signed, design units.</summary>
    public short ReadFWord() => ReadInt16();

    /// <summary>UFWORD: 16-bit unsigned, design units.</summary>
    public ushort ReadUFWord() => ReadUInt16();

    /// <summary>4-byte tag (e.g. "cmap").</summary>
    public Tag ReadTag() => new(ReadUInt32());

    public ReadOnlySpan<byte> ReadBytes(int count)
    {
        var slice = _data.Slice(_pos, count);
        _pos += count;
        return slice;
    }
}

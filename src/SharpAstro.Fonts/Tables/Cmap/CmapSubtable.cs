using SharpAstro.Fonts.IO;

namespace SharpAstro.Fonts.Tables.Cmap;

/// <summary>
/// One cmap subtable. Implementations cover the formats commonly found in
/// modern OpenType fonts: 0, 4, 6, 10, 12. Formats 2 (high-byte mapping for
/// CJK), 8 (mixed 16/32), 13 (last-resort), and 14 (variation selectors)
/// land in later phases as needed.
/// </summary>
public abstract class CmapSubtable
{
    public ushort PlatformId { get; }
    public ushort EncodingId { get; }
    public ushort Format { get; }

    protected CmapSubtable(ushort platformId, ushort encodingId, ushort format)
    {
        PlatformId = platformId;
        EncodingId = encodingId;
        Format = format;
    }

    /// <summary>
    /// Map a Unicode codepoint (or whatever this subtable's encoding uses) to
    /// a glyph index. Returns 0 (.notdef) if not mapped.
    /// </summary>
    public abstract uint GetGlyphId(uint codepoint);

    internal static CmapSubtable? TryParse(ReadOnlySpan<byte> tableData, int offset,
        ushort platformId, ushort encodingId)
    {
        if (offset + 2 > tableData.Length) return null;
        var r = new BigEndianReader(tableData, offset);
        var format = r.ReadUInt16();
        return format switch
        {
            0 => Format0Subtable.Parse(tableData, offset, platformId, encodingId),
            4 => Format4Subtable.Parse(tableData, offset, platformId, encodingId),
            6 => Format6Subtable.Parse(tableData, offset, platformId, encodingId),
            12 => Format12Subtable.Parse(tableData, offset, platformId, encodingId),
            _ => null, // unsupported; ignored
        };
    }
}

/// <summary>Format 0 — byte encoding table (256-entry direct lookup).</summary>
internal sealed class Format0Subtable : CmapSubtable
{
    private readonly byte[] _glyphIdArray;

    private Format0Subtable(ushort plat, ushort enc, byte[] arr)
        : base(plat, enc, 0) => _glyphIdArray = arr;

    public override uint GetGlyphId(uint codepoint)
        => codepoint < 256 ? _glyphIdArray[codepoint] : 0u;

    internal static Format0Subtable Parse(ReadOnlySpan<byte> data, int offset,
        ushort plat, ushort enc)
    {
        var r = new BigEndianReader(data, offset);
        // format(uint16) + length(uint16) + language(uint16)
        r.Skip(6);
        var arr = r.ReadBytes(256).ToArray();
        return new Format0Subtable(plat, enc, arr);
    }
}

/// <summary>Format 4 — segmented mapping for BMP Unicode. The most common.</summary>
internal sealed class Format4Subtable : CmapSubtable
{
    // Segment arrays: one entry per segment.
    private readonly ushort[] _endCode;
    private readonly ushort[] _startCode;
    private readonly short[] _idDelta;
    private readonly ushort[] _idRangeOffset;
    private readonly ushort[] _glyphIdArray;
    // Absolute byte offset within tableData where idRangeOffset[] starts
    // (needed for the spec's pointer arithmetic).
    private readonly int _idRangeOffsetStart;

    private Format4Subtable(ushort plat, ushort enc, ushort[] endCode, ushort[] startCode,
        short[] idDelta, ushort[] idRangeOffset, ushort[] glyphIdArray, int idRangeOffsetStart)
        : base(plat, enc, 4)
    {
        _endCode = endCode;
        _startCode = startCode;
        _idDelta = idDelta;
        _idRangeOffset = idRangeOffset;
        _glyphIdArray = glyphIdArray;
        _idRangeOffsetStart = idRangeOffsetStart;
    }

    public override uint GetGlyphId(uint codepoint)
    {
        if (codepoint > 0xFFFF) return 0u;
        var c = (ushort)codepoint;

        // Binary search for first segment whose endCode >= c.
        int lo = 0, hi = _endCode.Length - 1;
        while (lo < hi)
        {
            var mid = (lo + hi) >>> 1;
            if (_endCode[mid] < c) lo = mid + 1;
            else hi = mid;
        }
        if (_startCode[lo] > c) return 0u;

        var idro = _idRangeOffset[lo];
        if (idro == 0)
            return (ushort)((_idDelta[lo] + c) & 0xFFFF);

        // Spec pointer arithmetic:
        //   *(idRangeOffset[i]/2 + (c - startCode[i]) + &idRangeOffset[i])
        // Translated to indices into _glyphIdArray (which immediately follows
        // idRangeOffset[] in the original table).
        var offsetWithinIdRange = idro / 2 + (c - _startCode[lo]);
        // Index into the glyphIdArray relative to its start.
        // idRangeOffset[i] is from the start of idRangeOffset[i].
        // Number of remaining idRangeOffset entries from i: (segCount - i).
        // So glyphIdArray index = offsetWithinIdRange - (segCount - i).
        var glyphIndex = offsetWithinIdRange - (_idRangeOffset.Length - lo);
        if (glyphIndex < 0 || glyphIndex >= _glyphIdArray.Length) return 0u;

        var raw = _glyphIdArray[glyphIndex];
        if (raw == 0) return 0u;
        return (uint)((raw + _idDelta[lo]) & 0xFFFF);
    }

    internal static Format4Subtable Parse(ReadOnlySpan<byte> data, int offset,
        ushort plat, ushort enc)
    {
        var r = new BigEndianReader(data, offset);
        // format(uint16)
        r.Skip(2);
        var length = r.ReadUInt16();
        // language(uint16)
        r.Skip(2);
        var segCountX2 = r.ReadUInt16();
        var segCount = segCountX2 / 2;
        // searchRange + entrySelector + rangeShift (all uint16)
        r.Skip(6);

        var endCode = new ushort[segCount];
        for (var i = 0; i < segCount; i++) endCode[i] = r.ReadUInt16();
        // reservedPad (uint16)
        r.Skip(2);
        var startCode = new ushort[segCount];
        for (var i = 0; i < segCount; i++) startCode[i] = r.ReadUInt16();
        var idDelta = new short[segCount];
        for (var i = 0; i < segCount; i++) idDelta[i] = r.ReadInt16();

        var idRangeOffsetStart = r.Position;
        var idRangeOffset = new ushort[segCount];
        for (var i = 0; i < segCount; i++) idRangeOffset[i] = r.ReadUInt16();

        // Remaining bytes within the subtable form glyphIdArray.
        var subtableEnd = offset + length;
        var glyphIdBytes = subtableEnd - r.Position;
        if (glyphIdBytes < 0) glyphIdBytes = 0;
        var glyphIdArray = new ushort[glyphIdBytes / 2];
        for (var i = 0; i < glyphIdArray.Length; i++) glyphIdArray[i] = r.ReadUInt16();

        return new Format4Subtable(plat, enc, endCode, startCode, idDelta, idRangeOffset,
            glyphIdArray, idRangeOffsetStart);
    }
}

/// <summary>Format 6 — trimmed table mapping (contiguous range, BMP).</summary>
internal sealed class Format6Subtable : CmapSubtable
{
    private readonly ushort _firstCode;
    private readonly ushort[] _glyphIdArray;

    private Format6Subtable(ushort plat, ushort enc, ushort firstCode, ushort[] arr)
        : base(plat, enc, 6)
    {
        _firstCode = firstCode;
        _glyphIdArray = arr;
    }

    public override uint GetGlyphId(uint codepoint)
    {
        if (codepoint < _firstCode) return 0u;
        var idx = codepoint - _firstCode;
        if (idx >= (uint)_glyphIdArray.Length) return 0u;
        return _glyphIdArray[idx];
    }

    internal static Format6Subtable Parse(ReadOnlySpan<byte> data, int offset,
        ushort plat, ushort enc)
    {
        var r = new BigEndianReader(data, offset);
        // format(uint16) + length(uint16) + language(uint16)
        r.Skip(6);
        var firstCode = r.ReadUInt16();
        var entryCount = r.ReadUInt16();
        var arr = new ushort[entryCount];
        for (var i = 0; i < entryCount; i++) arr[i] = r.ReadUInt16();
        return new Format6Subtable(plat, enc, firstCode, arr);
    }
}

/// <summary>Format 12 — segmented coverage (full UCS-4).</summary>
internal sealed class Format12Subtable : CmapSubtable
{
    private readonly uint[] _startCharCode;
    private readonly uint[] _endCharCode;
    private readonly uint[] _startGlyphId;

    private Format12Subtable(ushort plat, ushort enc,
        uint[] startCC, uint[] endCC, uint[] startGid)
        : base(plat, enc, 12)
    {
        _startCharCode = startCC;
        _endCharCode = endCC;
        _startGlyphId = startGid;
    }

    public override uint GetGlyphId(uint codepoint)
    {
        if (_startCharCode.Length == 0) return 0u;
        int lo = 0, hi = _startCharCode.Length - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >>> 1;
            if (codepoint < _startCharCode[mid]) hi = mid - 1;
            else if (codepoint > _endCharCode[mid]) lo = mid + 1;
            else return _startGlyphId[mid] + (codepoint - _startCharCode[mid]);
        }
        return 0u;
    }

    internal static Format12Subtable Parse(ReadOnlySpan<byte> data, int offset,
        ushort plat, ushort enc)
    {
        var r = new BigEndianReader(data, offset);
        // format(uint16) + reserved(uint16) + length(uint32) + language(uint32)
        r.Skip(12);
        var numGroups = r.ReadUInt32();
        var startCC = new uint[numGroups];
        var endCC = new uint[numGroups];
        var startGid = new uint[numGroups];
        for (var i = 0; i < numGroups; i++)
        {
            startCC[i] = r.ReadUInt32();
            endCC[i] = r.ReadUInt32();
            startGid[i] = r.ReadUInt32();
        }
        return new Format12Subtable(plat, enc, startCC, endCC, startGid);
    }
}

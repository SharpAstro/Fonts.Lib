using SharpAstro.Fonts.IO;

namespace SharpAstro.Fonts.Tables.Cmap;

/// <summary>
/// One cmap subtable. Implementations cover the formats commonly found in
/// modern OpenType fonts: 0, 4, 6, 12, 14. Formats 2 (high-byte mapping for
/// CJK), 8 (mixed 16/32), and 13 (last-resort) land in later phases as needed.
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

    /// <summary>
    /// Parse the subtable at <paramref name="offset"/>, or null when the format is unsupported
    /// or the subtable's declared counts don't fit the physical table. A malformed subtable is
    /// dropped, never fatal: PDF subset fonts routinely truncate or overstate one subtable while
    /// the others are fine, and rejecting the whole font over it downgrades every glyph to a
    /// system-face fallback. Each format parser bounds-checks before reading — no exceptions
    /// as control flow.
    /// </summary>
    internal static CmapSubtable? TryParse(ReadOnlySpan<byte> tableData, int offset,
        ushort platformId, ushort encodingId)
    {
        if (offset < 0 || offset + 2 > tableData.Length) return null;
        var r = new BigEndianReader(tableData, offset);
        var format = r.ReadUInt16();
        return format switch
        {
            0 => Format0Subtable.TryParse(tableData, offset, platformId, encodingId),
            4 => Format4Subtable.TryParse(tableData, offset, platformId, encodingId),
            6 => Format6Subtable.TryParse(tableData, offset, platformId, encodingId),
            12 => Format12Subtable.TryParse(tableData, offset, platformId, encodingId),
            14 => Format14Subtable.TryParse(tableData, offset, platformId, encodingId),
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

    internal static new Format0Subtable? TryParse(ReadOnlySpan<byte> data, int offset,
        ushort plat, ushort enc)
    {
        // format(uint16) + length(uint16) + language(uint16) + glyphIdArray[256]
        if (offset + 6 + 256 > data.Length) return null;
        var r = new BigEndianReader(data, offset);
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

    internal static new Format4Subtable? TryParse(ReadOnlySpan<byte> data, int offset,
        ushort plat, ushort enc)
    {
        // Header: format + length + language + segCountX2 + searchRange + entrySelector
        // + rangeShift (7 × uint16).
        if (offset + 14 > data.Length) return null;
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

        // The four segment arrays (endCode, startCode, idDelta, idRangeOffset — segCountX2
        // bytes each) plus the reservedPad between endCode and startCode must fit; a table
        // truncated inside them has no usable mappings.
        if (offset + 16 + 4 * segCountX2 > data.Length) return null;

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

        // Remaining bytes within the subtable form glyphIdArray. The declared length is not
        // trusted past the physical table: PDF subsetters overstate it (Canon's 2008-era
        // subsets declare +6 bytes), and a font whose mappings are otherwise intact would be
        // rejected for those phantom bytes. Lookups already bounds-check glyphIndex, so any
        // genuinely missing tail entries just resolve to .notdef.
        var subtableEnd = Math.Min(offset + length, data.Length);
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

    internal static new Format6Subtable? TryParse(ReadOnlySpan<byte> data, int offset,
        ushort plat, ushort enc)
    {
        // format(uint16) + length(uint16) + language(uint16) + firstCode + entryCount
        if (offset + 10 > data.Length) return null;
        var r = new BigEndianReader(data, offset);
        r.Skip(6);
        var firstCode = r.ReadUInt16();
        var entryCount = r.ReadUInt16();
        if (offset + 10 + entryCount * 2 > data.Length) return null;
        var arr = new ushort[entryCount];
        for (var i = 0; i < entryCount; i++) arr[i] = r.ReadUInt16();
        return new Format6Subtable(plat, enc, firstCode, arr);
    }
}

/// <summary>
/// Format 14 — Unicode Variation Sequences (UVS).
/// Maps (base codepoint, variation selector) pairs to glyph IDs.
/// Used by CJK fonts for Ideographic Variation Sequences (IVS) where the same
/// base codepoint renders as different glyphs depending on the variation selector
/// (e.g. U+E0100–U+E01EF for CJK regional variants).
///
/// This subtable does NOT participate in normal <see cref="GetGlyphId"/> lookups.
/// Instead, callers use <see cref="GetVariationGlyphId"/> with a variation selector.
/// </summary>
internal sealed class Format14Subtable : CmapSubtable
{
    /// <summary>
    /// One variation selector record: the selector codepoint and its
    /// default/non-default UVS offset pairs.
    /// </summary>
    private readonly struct VarSelectorRecord
    {
        public readonly uint VarSelector;
        /// <summary>Sorted array of (startUnicodeValue, additionalCount) for default UVS.</summary>
        public readonly (uint Start, byte Count)[] DefaultRanges;
        /// <summary>Sorted array of (unicodeValue, glyphID) for non-default UVS.</summary>
        public readonly (uint Unicode, uint GlyphId)[] NonDefaultMappings;

        public VarSelectorRecord(uint varSelector,
            (uint, byte)[] defaultRanges,
            (uint, uint)[] nonDefaultMappings)
        {
            VarSelector = varSelector;
            DefaultRanges = defaultRanges;
            NonDefaultMappings = nonDefaultMappings;
        }
    }

    private readonly VarSelectorRecord[] _records;

    private Format14Subtable(ushort plat, ushort enc, VarSelectorRecord[] records)
        : base(plat, enc, 14) => _records = records;

    /// <summary>
    /// Format 14 does not support plain codepoint→GID lookups.
    /// Always returns 0; use <see cref="GetVariationGlyphId"/> instead.
    /// </summary>
    public override uint GetGlyphId(uint codepoint) => 0u;

    /// <summary>
    /// Result of a variation selector lookup.
    /// </summary>
    public enum VariationResult
    {
        /// <summary>The (base, selector) pair is not defined in this subtable.</summary>
        NotDefined,
        /// <summary>Use the default glyph for this base codepoint (from a normal cmap subtable).</summary>
        UseDefault,
        /// <summary>Use the specific glyph ID in <see cref="GetVariationGlyphId"/>.</summary>
        Found,
    }

    /// <summary>
    /// Look up a (base codepoint, variation selector) pair.
    /// Returns the result type and the glyph ID (only meaningful when result is <see cref="VariationResult.Found"/>).
    /// </summary>
    public VariationResult GetVariationGlyphId(uint codepoint, uint variationSelector, out uint glyphId)
    {
        glyphId = 0;

        // Binary search for the variation selector record.
        int lo = 0, hi = _records.Length - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >>> 1;
            var vs = _records[mid].VarSelector;
            if (variationSelector < vs) hi = mid - 1;
            else if (variationSelector > vs) lo = mid + 1;
            else
            {
                ref readonly var rec = ref _records[mid];

                // Check non-default mappings first (explicit glyph override).
                var ndm = rec.NonDefaultMappings;
                int nlo = 0, nhi = ndm.Length - 1;
                while (nlo <= nhi)
                {
                    var nmid = (nlo + nhi) >>> 1;
                    if (codepoint < ndm[nmid].Unicode) nhi = nmid - 1;
                    else if (codepoint > ndm[nmid].Unicode) nlo = nmid + 1;
                    else
                    {
                        glyphId = ndm[nmid].GlyphId;
                        return VariationResult.Found;
                    }
                }

                // Check default UVS ranges.
                var dr = rec.DefaultRanges;
                int dlo = 0, dhi = dr.Length - 1;
                while (dlo <= dhi)
                {
                    var dmid = (dlo + dhi) >>> 1;
                    var start = dr[dmid].Start;
                    var end = start + dr[dmid].Count; // additionalCount, so range is [start, start+count]
                    if (codepoint < start) dhi = dmid - 1;
                    else if (codepoint > end) dlo = dmid + 1;
                    else return VariationResult.UseDefault;
                }

                return VariationResult.NotDefined;
            }
        }

        return VariationResult.NotDefined;
    }

    internal static new Format14Subtable? TryParse(ReadOnlySpan<byte> data, int offset,
        ushort plat, ushort enc)
    {
        // format(uint16) + length(uint32) + numVarSelectorRecords(uint32)
        if (offset + 10 > data.Length) return null;
        var r = new BigEndianReader(data, offset);
        // format (uint16) = 14
        r.Skip(2);
        // length (uint32) — total byte length of this subtable
        r.Skip(4);
        var numVarSelectorRecords = r.ReadUInt32();
        // Selector records are 11 bytes each (uint24 + uint32 + uint32); also rejects a
        // garbage count before it turns into a giant allocation.
        if (numVarSelectorRecords > (uint)(data.Length - offset - 10) / 11) return null;

        var records = new VarSelectorRecord[numVarSelectorRecords];
        // First pass: read the selector records.
        var selectorEntries = new (uint VarSelector, uint DefaultUVSOffset, uint NonDefaultUVSOffset)[numVarSelectorRecords];
        for (var i = 0; i < numVarSelectorRecords; i++)
        {
            var varSelector = r.ReadUInt24();
            var defaultUVSOffset = r.ReadUInt32();
            var nonDefaultUVSOffset = r.ReadUInt32();
            selectorEntries[i] = (varSelector, defaultUVSOffset, nonDefaultUVSOffset);
        }

        // Second pass: parse default and non-default UVS tables. Each is offset-addressed
        // with its own count, so each gets its own fit check.
        for (var i = 0; i < numVarSelectorRecords; i++)
        {
            var (varSelector, defaultOff, nonDefaultOff) = selectorEntries[i];

            // Default UVS table: numUnicodeValueRanges(uint32), then
            // (startUnicodeValue uint24, additionalCount uint8) per range.
            (uint, byte)[] defaultRanges;
            if (defaultOff != 0)
            {
                var tableStart = offset + (int)defaultOff;
                if (tableStart < 0 || tableStart + 4 > data.Length) return null;
                var dr = new BigEndianReader(data, tableStart);
                var numRanges = dr.ReadUInt32();
                if (numRanges > (uint)(data.Length - tableStart - 4) / 4) return null;
                defaultRanges = new (uint, byte)[numRanges];
                for (var j = 0; j < numRanges; j++)
                {
                    var start = dr.ReadUInt24();
                    var count = dr.ReadByte();
                    defaultRanges[j] = (start, count);
                }
            }
            else
            {
                defaultRanges = [];
            }

            // Non-default UVS table: numUVSMappings(uint32), then
            // (unicodeValue uint24, glyphID uint16) per mapping.
            (uint, uint)[] nonDefaultMappings;
            if (nonDefaultOff != 0)
            {
                var tableStart = offset + (int)nonDefaultOff;
                if (tableStart < 0 || tableStart + 4 > data.Length) return null;
                var nr = new BigEndianReader(data, tableStart);
                var numMappings = nr.ReadUInt32();
                if (numMappings > (uint)(data.Length - tableStart - 4) / 5) return null;
                nonDefaultMappings = new (uint, uint)[numMappings];
                for (var j = 0; j < numMappings; j++)
                {
                    var unicode = nr.ReadUInt24();
                    var gid = nr.ReadUInt16();
                    nonDefaultMappings[j] = (unicode, gid);
                }
            }
            else
            {
                nonDefaultMappings = [];
            }

            records[i] = new VarSelectorRecord(varSelector, defaultRanges, nonDefaultMappings);
        }

        return new Format14Subtable(plat, enc, records);
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

    internal static new Format12Subtable? TryParse(ReadOnlySpan<byte> data, int offset,
        ushort plat, ushort enc)
    {
        // format(uint16) + reserved(uint16) + length(uint32) + language(uint32) + numGroups(uint32)
        if (offset + 16 > data.Length) return null;
        var r = new BigEndianReader(data, offset);
        r.Skip(12);
        var numGroups = r.ReadUInt32();
        // Also rejects a hostile/garbage count before it turns into a giant allocation.
        if (numGroups > (uint)(data.Length - offset - 16) / 12) return null;
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

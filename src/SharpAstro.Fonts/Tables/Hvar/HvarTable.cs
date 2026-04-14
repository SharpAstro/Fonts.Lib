using SharpAstro.Fonts.IO;
using SharpAstro.Fonts.Variations;

namespace SharpAstro.Fonts.Tables.Hvar;

/// <summary>
/// Horizontal Metrics Variations table ('HVAR'). Contains an
/// <see cref="ItemVariationStore"/> for per-glyph advance-width (and
/// optionally LSB/RSB) deltas across variation axes.
/// </summary>
internal sealed class HvarTable
{
    private readonly ItemVariationStore _ivs;
    /// <summary>Maps glyphId → (outer, inner) for advance width deltas.
    /// Null = identity mapping (outer=0, inner=glyphId).</summary>
    private readonly DeltaSetIndexMap? _advanceWidthMap;

    private HvarTable(ItemVariationStore ivs, DeltaSetIndexMap? advanceWidthMap)
    {
        _ivs = ivs;
        _advanceWidthMap = advanceWidthMap;
    }

    /// <summary>The underlying Item Variation Store — exposed so other consumers
    /// (COLR, MVAR, etc.) can share it if embedded in the same table.</summary>
    public ItemVariationStore VariationStore => _ivs;

    /// <summary>Get the advance-width delta for <paramref name="glyphId"/>
    /// at the given normalized axis coordinates.</summary>
    public float GetAdvanceWidthDelta(uint glyphId, ReadOnlySpan<float> normalizedCoords)
    {
        int outer, inner;
        if (_advanceWidthMap is not null)
            (outer, inner) = _advanceWidthMap.Map(glyphId);
        else
            (outer, inner) = (0, (int)glyphId);
        return _ivs.GetDelta(outer, inner, normalizedCoords);
    }

    public static HvarTable Parse(ReadOnlySpan<byte> data)
    {
        var r = new BigEndianReader(data);
        var majorVersion = r.ReadUInt16();
        var minorVersion = r.ReadUInt16();
        var ivsOffset = r.ReadUInt32();
        var advWidthMapOffset = r.ReadUInt32();
        // lsbMappingOffset, rsbMappingOffset follow but we skip them for now.

        var ivs = ItemVariationStore.Parse(data[(int)ivsOffset..]);

        DeltaSetIndexMap? advMap = null;
        if (advWidthMapOffset != 0)
            advMap = DeltaSetIndexMap.Parse(data[(int)advWidthMapOffset..]);

        return new HvarTable(ivs, advMap);
    }
}

/// <summary>
/// DeltaSetIndexMap — maps a glyph index to an (outerIndex, innerIndex)
/// pair for Item Variation Store lookup.
/// </summary>
internal sealed class DeltaSetIndexMap
{
    private readonly uint[] _entries; // packed: (outer << 16) | inner

    private DeltaSetIndexMap(uint[] entries) => _entries = entries;

    public (int outer, int inner) Map(uint glyphId)
    {
        if (glyphId >= _entries.Length)
        {
            // Spec: if glyphId >= mapCount, use the last entry.
            if (_entries.Length == 0) return (0, (int)glyphId);
            var last = _entries[^1];
            return ((int)(last >> 16), (int)(last & 0xFFFF));
        }
        var entry = _entries[glyphId];
        return ((int)(entry >> 16), (int)(entry & 0xFFFF));
    }

    public static DeltaSetIndexMap Parse(ReadOnlySpan<byte> data)
    {
        var r = new BigEndianReader(data);
        var format = r.ReadByte();
        var entryFormat = r.ReadByte();
        var mapCount = format == 0 ? r.ReadUInt16() : r.ReadUInt32();

        // entryFormat: bits 4-7 = (entrySize - 1), bits 0-3 = innerBitCount
        var entrySize = ((entryFormat >> 4) & 0xF) + 1;
        var innerBits = entryFormat & 0xF;
        var innerMask = (1u << innerBits) - 1;

        var entries = new uint[mapCount];
        for (var i = 0; i < mapCount; i++)
        {
            uint raw = 0;
            for (var b = 0; b < entrySize; b++)
                raw = (raw << 8) | r.ReadByte();
            entries[i] = ((raw >> innerBits) << 16) | (raw & innerMask);
        }
        return new DeltaSetIndexMap(entries);
    }
}

using SharpAstro.Fonts.IO;

namespace SharpAstro.Fonts.Variations;

/// <summary>
/// OpenType Item Variation Store — the core structure backing HVAR, MVAR,
/// COLR v1 Var* paints, and cvar. Stores per-item deltas organized by
/// variation regions; <see cref="GetDelta"/> evaluates them against a set
/// of normalized axis coordinates.
///
/// <para>Parsed from a binary blob that can live inside GDEF, HVAR, MVAR,
/// COLR, or any table that carries variation data.</para>
/// </summary>
internal sealed class ItemVariationStore
{
    private readonly VariationRegion[] _regions;
    private readonly ItemVariationData[] _subtables;

    private ItemVariationStore(VariationRegion[] regions, ItemVariationData[] subtables)
    {
        _regions = regions;
        _subtables = subtables;
    }

    /// <summary>Number of ItemVariationData subtables (outer index range).</summary>
    public int SubtableCount => _subtables.Length;

    /// <summary>
    /// Evaluate the delta for item (<paramref name="outerIndex"/>,
    /// <paramref name="innerIndex"/>) at the given normalized axis coordinates.
    /// Returns 0 for out-of-range indices or when no variation applies.
    /// </summary>
    public float GetDelta(int outerIndex, int innerIndex, ReadOnlySpan<float> normalizedCoords)
    {
        if ((uint)outerIndex >= (uint)_subtables.Length) return 0f;
        var sub = _subtables[outerIndex];
        if ((uint)innerIndex >= (uint)sub.ItemCount) return 0f;

        var delta = 0f;
        var regionIndices = sub.RegionIndices;
        var row = sub.GetRow(innerIndex);

        for (var r = 0; r < regionIndices.Length; r++)
        {
            var regionIdx = regionIndices[r];
            if ((uint)regionIdx >= (uint)_regions.Length) continue;
            var scalar = _regions[regionIdx].EvaluateScalar(normalizedCoords);
            if (scalar == 0f) continue;
            delta += row[r] * scalar;
        }
        return delta;
    }

    /// <summary>Parse an ItemVariationStore from the given data span.</summary>
    public static ItemVariationStore Parse(ReadOnlySpan<byte> data)
    {
        var r = new BigEndianReader(data);
        var format = r.ReadUInt16(); // must be 1
        var regionListOffset = r.ReadUInt32();
        var subtableCount = r.ReadUInt16();
        var subtableOffsets = new uint[subtableCount];
        for (var i = 0; i < subtableCount; i++)
            subtableOffsets[i] = r.ReadUInt32();

        // Parse VariationRegionList
        var regions = ParseRegionList(data[(int)regionListOffset..]);

        // Parse ItemVariationData subtables
        var subtables = new ItemVariationData[subtableCount];
        for (var i = 0; i < subtableCount; i++)
            subtables[i] = ItemVariationData.Parse(data[(int)subtableOffsets[i]..]);

        return new ItemVariationStore(regions, subtables);
    }

    private static VariationRegion[] ParseRegionList(ReadOnlySpan<byte> data)
    {
        var r = new BigEndianReader(data);
        var axisCount = r.ReadUInt16();
        var regionCount = r.ReadUInt16();
        var regions = new VariationRegion[regionCount];
        for (var i = 0; i < regionCount; i++)
        {
            var axes = new RegionAxisCoords[axisCount];
            for (var a = 0; a < axisCount; a++)
            {
                axes[a] = new RegionAxisCoords(
                    StartCoord: r.ReadF2Dot14(),
                    PeakCoord: r.ReadF2Dot14(),
                    EndCoord: r.ReadF2Dot14());
            }
            regions[i] = new VariationRegion(axes);
        }
        return regions;
    }
}

/// <summary>Per-axis region bounds (F2.14 normalized coordinates).</summary>
internal readonly record struct RegionAxisCoords(float StartCoord, float PeakCoord, float EndCoord);

/// <summary>A variation region defined by per-axis start/peak/end triples.</summary>
internal sealed class VariationRegion
{
    private readonly RegionAxisCoords[] _axes;
    public VariationRegion(RegionAxisCoords[] axes) => _axes = axes;

    /// <summary>
    /// Compute the scalar for this region given normalized axis coordinates.
    /// Same piecewise-linear formula as gvar's <c>TupleVariation.ComputeScalar</c>.
    /// </summary>
    public float EvaluateScalar(ReadOnlySpan<float> normalizedCoords)
    {
        var s = 1f;
        for (var i = 0; i < _axes.Length && i < normalizedCoords.Length; i++)
        {
            var ax = _axes[i];
            if (ax.PeakCoord == 0f) continue;           // neutral axis

            var v = normalizedCoords[i];
            if (v == ax.PeakCoord) continue;             // exactly at peak
            if (v <= ax.StartCoord || v >= ax.EndCoord)
                return 0f;                                // outside region
            if (v < ax.PeakCoord)
                s *= (v - ax.StartCoord) / (ax.PeakCoord - ax.StartCoord);
            else
                s *= (ax.EndCoord - v) / (ax.EndCoord - ax.PeakCoord);
        }
        return s;
    }
}

/// <summary>One ItemVariationData subtable — a 2D array of deltas.</summary>
internal sealed class ItemVariationData
{
    private readonly int[] _deltas;       // flat [itemCount × regionCount]
    private readonly ushort[] _regionIndices;
    private readonly int _regionCount;

    public int ItemCount { get; }
    public ReadOnlySpan<ushort> RegionIndices => _regionIndices;

    private ItemVariationData(int itemCount, ushort[] regionIndices, int[] deltas)
    {
        ItemCount = itemCount;
        _regionIndices = regionIndices;
        _regionCount = regionIndices.Length;
        _deltas = deltas;
    }

    /// <summary>Get the delta row for item <paramref name="innerIndex"/>.</summary>
    public ReadOnlySpan<int> GetRow(int innerIndex)
        => _deltas.AsSpan(innerIndex * _regionCount, _regionCount);

    public static ItemVariationData Parse(ReadOnlySpan<byte> data)
    {
        var r = new BigEndianReader(data);
        var itemCount = r.ReadUInt16();
        var wordDeltaCount = r.ReadUInt16();
        var regionIndexCount = r.ReadUInt16();

        var regionIndices = new ushort[regionIndexCount];
        for (var i = 0; i < regionIndexCount; i++)
            regionIndices[i] = r.ReadUInt16();

        // wordDeltaCount: bit 15 = LONG_WORDS flag, bits 0-14 = count of "word" columns.
        var longWords = (wordDeltaCount & 0x8000) != 0;
        var wordCount = wordDeltaCount & 0x7FFF;
        // Clamp wordCount to regionIndexCount (spec says it should not exceed).
        if (wordCount > regionIndexCount) wordCount = regionIndexCount;

        var deltas = new int[itemCount * regionIndexCount];
        for (var row = 0; row < itemCount; row++)
        {
            var baseIdx = row * regionIndexCount;
            for (var col = 0; col < regionIndexCount; col++)
            {
                if (col < wordCount)
                    deltas[baseIdx + col] = longWords ? r.ReadInt32() : r.ReadInt16();
                else
                    deltas[baseIdx + col] = longWords ? r.ReadInt16() : r.ReadSByte();
            }
        }

        return new ItemVariationData(itemCount, regionIndices, deltas);
    }
}

using SharpAstro.Fonts.IO;

namespace SharpAstro.Fonts.Tables.Kern;

/// <summary>
/// Legacy 'kern' table — provides pairwise kerning adjustments in FUnits.
/// Implements format 0 (ordered pairs) which covers the vast majority of
/// real-world fonts that still ship a 'kern' table. Format 2 (class-based)
/// is rare and currently unsupported.
///
/// <para>Fonts with GPOS pair adjustment (lookup type 2) should prefer that
/// over 'kern'. This table exists as a fallback for fonts lacking GPOS.</para>
/// </summary>
internal sealed class KernTable
{
    /// <summary>Sorted array of (packed glyph pair, kern value) for binary search.</summary>
    private readonly (uint pair, short value)[] _pairs;

    private KernTable((uint pair, short value)[] pairs) => _pairs = pairs;

    /// <summary>Pack two glyph IDs into a single uint for lookup.</summary>
    private static uint PackPair(uint left, uint right) => (left << 16) | (right & 0xFFFF);

    /// <summary>
    /// Get the kerning value (in FUnits) for the glyph pair
    /// (<paramref name="left"/>, <paramref name="right"/>). Returns 0 if no
    /// kerning pair exists.
    /// </summary>
    public int GetKerning(uint left, uint right)
    {
        var key = PackPair(left, right);
        // Binary search — pairs are sorted by packed key.
        int lo = 0, hi = _pairs.Length - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >>> 1;
            var cmp = _pairs[mid].pair.CompareTo(key);
            if (cmp == 0) return _pairs[mid].value;
            if (cmp < 0) lo = mid + 1;
            else hi = mid - 1;
        }
        return 0;
    }

    /// <summary>Parse a 'kern' table. Accumulates all format-0 horizontal
    /// kerning subtables into one merged pair array.</summary>
    public static KernTable Parse(ReadOnlySpan<byte> data)
    {
        var r = new BigEndianReader(data);
        var version = r.ReadUInt16();

        // Microsoft kern table: version 0, nTables follows as uint16.
        // Apple kern table: version 1.0 (uint32), nTables as uint32 — rare; we handle v0 only.
        if (version != 0)
            return new KernTable([]);

        var nTables = r.ReadUInt16();
        var allPairs = new List<(uint pair, short value)>();

        for (var t = 0; t < nTables; t++)
        {
            var subtableVersion = r.ReadUInt16(); // always 0
            var length = r.ReadUInt16();
            var coverage = r.ReadUInt16();
            var format = coverage >> 8;
            var isHorizontal = (coverage & 0x01) != 0;
            var isMinimum = (coverage & 0x02) != 0;
            var isCrossStream = (coverage & 0x04) != 0;
            var isOverride = (coverage & 0x08) != 0;

            // Only format 0 (ordered pairs), horizontal, not cross-stream.
            if (format != 0 || !isHorizontal || isCrossStream)
            {
                // Skip remainder of this subtable (length includes the 6-byte header we just read).
                r.Skip(Math.Max(0, length - 6));
                continue;
            }

            var nPairs = r.ReadUInt16();
            r.Skip(6); // searchRange, entrySelector, rangeShift

            for (var i = 0; i < nPairs; i++)
            {
                var left = r.ReadUInt16();
                var right = r.ReadUInt16();
                var value = r.ReadInt16();
                allPairs.Add((PackPair(left, right), value));
            }
        }

        // Sort for binary search (should already be sorted per spec, but be safe).
        allPairs.Sort((a, b) => a.pair.CompareTo(b.pair));
        return new KernTable(allPairs.ToArray());
    }
}

namespace SharpAstro.Fonts.Shaping.Ucd;

/// <summary>
/// Binary-search accessors over the packed little-endian RVA blobs emitted by
/// <c>tools/UcdGen</c>. Two blob shapes are used:
///
/// <list type="bullet">
/// <item><b>Range table</b> — 7-byte entries <c>[start:u24][end:u24][value:u8]</c>, sorted
/// by <c>start</c>, non-overlapping. Maps a codepoint to a byte property value
/// (Canonical_Combining_Class, Joining_Type).</item>
/// <item><b>Pair table</b> — 6-byte entries <c>[key:u24][value:u24]</c>, sorted by
/// <c>key</c>. Maps a codepoint to a codepoint (Bidi_Mirroring_Glyph).</item>
/// <item><b>Wide range table</b> — 10-byte entries <c>[start:u24][end:u24][value:u32]</c>,
/// sorted by <c>start</c>. Maps a codepoint to a 32-bit value (Script, as a packed tag).</item>
/// </list>
///
/// Codepoints fit in 24 bits (max U+10FFFF), so both blobs stay compact and are shared,
/// zero-allocation PE data pages (the tables are <see cref="System.ReadOnlySpan{T}"/> over
/// the assembly's data section).
/// </summary>
internal static class UcdTables
{
    private const int RangeEntrySize = 7;      // [start:3][end:3][value:1]
    private const int PairEntrySize = 6;       // [key:3][value:3]
    private const int WideRangeEntrySize = 10; // [start:3][end:3][value:4]

    private static uint ReadU24(ReadOnlySpan<byte> b, int offset)
        => (uint)(b[offset] | (b[offset + 1] << 8) | (b[offset + 2] << 16));

    private static uint ReadU32(ReadOnlySpan<byte> b, int offset)
        => (uint)(b[offset] | (b[offset + 1] << 8) | (b[offset + 2] << 16) | (b[offset + 3] << 24));

    /// <summary>Look up <paramref name="codepoint"/> in a range table, returning the entry's
    /// byte value or <paramref name="notFound"/> if no range contains it.</summary>
    internal static byte RangeByte(ReadOnlySpan<byte> ranges, uint codepoint, byte notFound)
    {
        int lo = 0, hi = ranges.Length / RangeEntrySize - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >> 1;
            var offset = mid * RangeEntrySize;
            var start = ReadU24(ranges, offset);
            if (codepoint < start) { hi = mid - 1; continue; }
            var end = ReadU24(ranges, offset + 3);
            if (codepoint > end) { lo = mid + 1; continue; }
            return ranges[offset + 6];
        }
        return notFound;
    }

    /// <summary>Look up <paramref name="codepoint"/> in a pair table, returning the mapped
    /// codepoint or <paramref name="notFound"/> if the key is absent.</summary>
    internal static uint PairValue(ReadOnlySpan<byte> pairs, uint codepoint, uint notFound)
    {
        int lo = 0, hi = pairs.Length / PairEntrySize - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >> 1;
            var offset = mid * PairEntrySize;
            var key = ReadU24(pairs, offset);
            if (codepoint < key) hi = mid - 1;
            else if (codepoint > key) lo = mid + 1;
            else return ReadU24(pairs, offset + 3);
        }
        return notFound;
    }

    /// <summary>Look up <paramref name="codepoint"/> in a wide range table, returning the entry's
    /// 32-bit value or <paramref name="notFound"/> if no range contains it.</summary>
    internal static uint RangeU32(ReadOnlySpan<byte> ranges, uint codepoint, uint notFound)
    {
        int lo = 0, hi = ranges.Length / WideRangeEntrySize - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >> 1;
            var offset = mid * WideRangeEntrySize;
            var start = ReadU24(ranges, offset);
            if (codepoint < start) { hi = mid - 1; continue; }
            var end = ReadU24(ranges, offset + 3);
            if (codepoint > end) { lo = mid + 1; continue; }
            return ReadU32(ranges, offset + 6);
        }
        return notFound;
    }

    /// <summary>
    /// Look up <paramref name="codepoint"/> in a wide range table via a page index — the
    /// pre-generated two-stage-trie technique. <paramref name="pageIndex"/> maps each 256-codepoint
    /// page (<c>cp &gt;&gt; 8</c>) to the index of the first range that reaches into it (u16, little-
    /// endian), so only the handful of ranges overlapping that page are scanned rather than binary-
    /// searching the whole table. Returns <paramref name="notFound"/> for a gap between ranges or a
    /// codepoint whose page is beyond the table.
    /// </summary>
    internal static uint RangeU32Paged(ReadOnlySpan<byte> ranges, ReadOnlySpan<byte> pageIndex,
        uint codepoint, uint notFound)
    {
        var page = codepoint >> 8;
        if (page >= (uint)(pageIndex.Length >> 1)) return notFound;
        var pi = (int)(page << 1);
        var i = pageIndex[pi] | (pageIndex[pi + 1] << 8);
        var count = ranges.Length / WideRangeEntrySize;
        while (i < count)
        {
            var offset = i * WideRangeEntrySize;
            var start = ReadU24(ranges, offset);
            if (start > codepoint) break;                       // fell into a gap before the next range
            if (codepoint <= ReadU24(ranges, offset + 3))       // within [start, end]
                return ReadU32(ranges, offset + 6);
            i++;
        }
        return notFound;
    }
}
